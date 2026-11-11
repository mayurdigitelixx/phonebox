using Microsoft.Extensions.Logging.Abstractions;
using PhoneBox.Agent;
using Xunit;

public sealed class ControlDeckTests
{
    [Fact]
    public void PushToTalkExpiresAndRestoresTheOriginalState()
    {
        long time = 0; var muted = true;
        var leases = new MicrophoneLeases(_ => muted, (_, value) => { muted = value; return true; }, () => time);
        var id = leases.Begin("microphone");
        Assert.NotNull(id); Assert.False(muted);
        time = 2999; leases.Sweep(); Assert.False(muted);
        time = 3000; leases.Sweep(); Assert.True(muted);
        Assert.False(leases.Renew(id!));
    }
    [Fact]
    public void OverlappingHoldsDoNotRestoreTheMicrophoneUntilTheLastRelease()
    {
        var muted = true;
        var leases = new MicrophoneLeases(_ => muted, (_, value) => { muted = value; return true; }, () => 0);
        var a = leases.Begin("microphone"); var b = leases.Begin("microphone");
        Assert.True(leases.End(a!)); Assert.False(muted);
        Assert.True(leases.End(b!)); Assert.True(muted);
    }
    [Fact]
    public void ADisconnectDoesNotMuteAPreviouslyUnmutedMicrophone()
    {
        var muted = false;
        var leases = new MicrophoneLeases(_ => muted, (_, value) => { muted = value; return true; }, () => 0);
        leases.Begin("microphone"); leases.Sweep(all: true); Assert.False(muted);
    }
    [Fact]
    public void RenewalsCannotKeepAMicrophoneOpenIndefinitely()
    {
        long time = 0; var muted = true;
        var leases = new MicrophoneLeases(_ => muted, (_, value) => { muted = value; return true; }, () => time);
        var id = leases.Begin("microphone")!;
        for (time = 1000; time < 60_000; time += 1000) Assert.True(leases.Renew(id));
        Assert.False(leases.Renew(id)); Assert.True(muted);
    }
    [Fact]
    public void PowerConfirmationIsDelayedBoundToTheActionAndSingleUse()
    {
        long now = 0; var power = new PowerConfirmations(() => now);
        var nonce = power.Arm("restart");
        Assert.False(power.Consume(nonce, "restart"));
        now = 2000; Assert.False(power.Consume(nonce, "shutdown"));
        Assert.True(power.Consume(nonce, "restart")); Assert.False(power.Consume(nonce, "restart"));
        nonce = power.Arm("sleep"); now += 6001; Assert.False(power.Consume(nonce, "sleep"));
    }
    [Fact]
    public void OscUsesPaddedStringsAndNetworkByteOrder()
    {
        Assert.Equal(new byte[] { 47, 102, 0, 0, 44, 102, 0, 0, 63, 128, 0, 0 }, ControlDeck.OscPacket("/f", 1));
        Assert.Throws<ArgumentException>(() => ControlDeck.OscPacket("not-an-address", 1));
    }
    [Fact]
    public void MidiBytesUseWireOrderAndRejectSystemOrOutOfRangeMessages()
    {
        Assert.Equal(0x6407B0, DeckMidi.Parse("B0 07 {value}",100));
        Assert.Equal(0x003CC0, DeckMidi.Parse("C0 3C",0));
        Assert.Throws<ArgumentException>(()=>DeckMidi.Parse("F0 00 00",0));
        Assert.Throws<ArgumentException>(()=>DeckMidi.Parse("90 FF 10",0));
        Assert.Throws<ArgumentException>(()=>DeckMidi.Parse("90 3C",0));
        Assert.Throws<ArgumentOutOfRangeException>(()=>DeckMidi.Parse("B0 07 {value}",128));
    }
    [Fact]
    public void PhoneConfigurationDoesNotExposeControlSecretsOrCommands()
    {
        var cfg = new AgentConfig { Controls = new() { SpotifyToken = "private-token", Actions = [new() { Id = "sound", Target = "private-file-path" }] } };
        var json = System.Text.Json.JsonSerializer.Serialize(cfg.ToPublic());
        Assert.DoesNotContain("private-token", json); Assert.DoesNotContain("private-file-path", json);
        Assert.True(Lan.AllowedOverLan("/api/controls/state"));
        Assert.False(Lan.AllowedOverLan("/api/controls/capture"));
    }

    [Fact]
    public async Task NowPlayingShowsActiveRadioAndNextPrevPlayPauseControlsCycleThroughStations()
    {
        var dir = Path.Combine(Path.GetTempPath(), "phonebox-radio-nowplaying-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var cfgPath = Path.Combine(dir, "config.json");
            File.WriteAllText(cfgPath, """
                {
                  "controls": {
                    "actions": [
                      { "id": "r-fenomen", "label": "Radyo Fenomen", "kind": "radio", "target": "https://live.radyofenomen.com/fenomen/128/icecast.audio", "value": 80 },
                      { "id": "r-kafa", "label": "Kafa Radyo", "kind": "radio", "target": "https://stream.radyotvonline.net/hls/play/kafaradyo.m3u8", "value": 75 },
                      { "id": "r-power", "label": "PowerTürk", "kind": "radio", "target": "https://listen.powerapp.com.tr/powerturk/mpeg/icecast.audio", "value": 85 }
                    ]
                  }
                }
                """);

            long now = 10_000;
            var native = new FakeRadioDeckNative();
            using var deck = new ControlDeck(new ConfigStore(cfgPath), native, NullLogger<ControlDeck>.Instance, () => now);

            var idleSys = new MediaSnapshot("Spotify", "Track 1", "Artist 1", "Album 1", false, 12_000, 180_000, 1, true);
            Assert.Equal(idleSys, deck.EffectiveMedia(idleSys));
            Assert.Null(await deck.TryControlRadioMediaAsync("next", idleSys, default));

            // 1. Start Radyo Fenomen -> EffectiveMedia reports Internet Radio with station name.
            Assert.True((await deck.Act("action", "r-fenomen", "", null, null, null, default)).Ok);
            now = 15_000;
            var m1 = deck.EffectiveMedia(idleSys);
            Assert.NotNull(m1);
            Assert.Equal("Internet Radio", m1!.App);
            Assert.Equal("Radyo Fenomen", m1.Title);
            Assert.Equal("Live stream", m1.Artist);
            Assert.True(m1.Playing);
            Assert.Equal(5_000, m1.PosMs);
            Assert.Equal(0, m1.DurMs);

            // 2. Next song button in Now Playing -> switches to Kafa Radyo.
            Assert.Equal(true, await deck.TryControlRadioMediaAsync("next", idleSys, default));
            Assert.Equal(["r-kafa"], native.PlayingRadios.ToArray());
            Assert.Equal("Kafa Radyo", deck.EffectiveMedia(idleSys)!.Title);

            // 3. Next song button again -> switches to PowerTürk.
            Assert.Equal(true, await deck.TryControlRadioMediaAsync("next", idleSys, default));
            Assert.Equal(["r-power"], native.PlayingRadios.ToArray());
            Assert.Equal("PowerTürk", deck.EffectiveMedia(idleSys)!.Title);

            // 4. Next song button at the end of list -> wraps around to Radyo Fenomen.
            Assert.Equal(true, await deck.TryControlRadioMediaAsync("next", idleSys, default));
            Assert.Equal(["r-fenomen"], native.PlayingRadios.ToArray());
            Assert.Equal("Radyo Fenomen", deck.EffectiveMedia(idleSys)!.Title);

            // 5. Previous song button at the first station -> wraps around backwards to PowerTürk.
            Assert.Equal(true, await deck.TryControlRadioMediaAsync("prev", idleSys, default));
            Assert.Equal(["r-power"], native.PlayingRadios.ToArray());
            Assert.Equal("PowerTürk", deck.EffectiveMedia(idleSys)!.Title);

            // 6. Previous song button again -> goes back to Kafa Radyo.
            Assert.Equal(true, await deck.TryControlRadioMediaAsync("prev", idleSys, default));
            Assert.Equal(["r-kafa"], native.PlayingRadios.ToArray());
            Assert.Equal("Kafa Radyo", deck.EffectiveMedia(idleSys)!.Title);

            // 7. Play/Pause button in Now Playing -> pauses Kafa Radyo, keeps station visible as Paused, then resumes on second press.
            now = 22_000;
            Assert.Equal(true, await deck.TryControlRadioMediaAsync("playpause", idleSys, default));
            Assert.Empty(native.PlayingRadios);
            var pausedMedia = deck.EffectiveMedia(idleSys);
            Assert.NotNull(pausedMedia);
            Assert.Equal("Internet Radio", pausedMedia!.App);
            Assert.Equal("Kafa Radyo", pausedMedia.Title);
            Assert.Equal("Paused", pausedMedia.Artist);
            Assert.False(pausedMedia.Playing);

            Assert.Equal(true, await deck.TryControlRadioMediaAsync("playpause", idleSys, default));
            Assert.Equal(["r-kafa"], native.PlayingRadios.ToArray());
            Assert.True(deck.EffectiveMedia(idleSys)!.Playing);

            // 8. If a station throws on Play, Next skips it and lands on the next working station.
            native.FailingRadios.Add("r-power");
            Assert.Equal(true, await deck.TryControlRadioMediaAsync("next", idleSys, default));
            Assert.Equal(["r-fenomen"], native.PlayingRadios.ToArray());
            Assert.Equal("Radyo Fenomen", deck.EffectiveMedia(idleSys)!.Title);

            // 9. Starting a radio pauses active system media ONLY after Play succeeds (not when Play fails).
            native.MediaSessions = [new DeckMedia("Spotify.exe", "Spotify", "Song", true, 0, 100000, true, true, true, false, "None")];
            native.PausedMediaIds.Clear();
            Assert.False((await deck.Act("action", "r-power", "", null, null, null, default)).Ok);
            Assert.Empty(native.PausedMediaIds);

            Assert.True((await deck.Act("action", "r-kafa", "", null, null, null, default)).Ok);
            Assert.Equal(["Spotify.exe"], native.PausedMediaIds.ToArray());

            // 10. Resuming system media stops active radio streams.
            native.MediaSessions = [new DeckMedia("Spotify.exe", "Spotify", "Song", false, 0, 100000, true, true, true, false, "None")];
            Assert.True((await deck.Act("media", "Spotify.exe", "play", null, null, null, default)).Ok);
            Assert.Empty(native.PlayingRadios);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    private sealed class FakeRadioDeckNative : IDeckNative
    {
        public readonly HashSet<string> PlayingRadios = [];
        public readonly HashSet<string> FailingRadios = [];
        public List<DeckMedia> MediaSessions = [];
        public readonly List<string> PausedMediaIds = [];

        public DeckAudio Audio() => new(true, null, [], []);
        public bool Device(string id, int? volume, bool? muted, int? channel = null) => true;
        public bool Session(string id, int? volume, bool? muted) => true;
        public bool DefaultDevice(string id, int? role = null) => true;
        public IReadOnlyList<DeckMonitor> Monitors() => [];
        public bool Brightness(string id, int value) => false;
        public IReadOnlyList<DeckItem> PowerPlans() => [];
        public Task<bool> PlatformAction(string kind, string target, string? argument, int value, CancellationToken ct) => Task.FromResult(false);
        public string? ActionState(DeckAction action) => action.Kind is "sound" or "radio"
            ? (PlayingRadios.Contains(action.Id) ? "Playing" : "Stopped")
            : null;
        public Task<IReadOnlyList<DeckMedia>> Media(CancellationToken ct) => Task.FromResult<IReadOnlyList<DeckMedia>>(MediaSessions);
        public Task<bool> MediaAction(string id, string action, long value, CancellationToken ct)
        {
            if (action == "pause") PausedMediaIds.Add(id);
            return Task.FromResult(true);
        }
        public void Play(string id, string file, string? device, int volume)
        {
            if (FailingRadios.Contains(id)) throw new InvalidOperationException("Stream open failed");
            PlayingRadios.Add(id);
        }
        public void StopSound(string? id)
        {
            if (id is null) PlayingRadios.Clear();
            else PlayingRadios.Remove(id);
        }
        public IReadOnlyList<DeckItem> MidiPorts() => [];
        public void Midi(int port, string message, int value) { }
        public async IAsyncEnumerable<DeckAudioPacket> Listen([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        { await Task.CompletedTask; yield break; }
        public void Dispose() => PlayingRadios.Clear();
    }
}

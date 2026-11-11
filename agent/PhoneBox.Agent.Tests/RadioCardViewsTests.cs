using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using PhoneBox.Agent;
using Xunit;

public sealed class RadioCardViewsTests
{
    [Fact]
    public async Task RadioLogoCacheResolvesStationsCachesRasterAndFallsBackToMonogramSvg()
    {
        Assert.NotEmpty(RadioLogoCache.FindCuratedUrls("PowerTürk", ""));
        Assert.NotEmpty(RadioLogoCache.FindCuratedUrls("Kafa Radyo (Backup)", ""));
        Assert.NotEmpty(RadioLogoCache.FindCuratedUrls("Radyo Fenomen", ""));
        Assert.NotEmpty(RadioLogoCache.FindCuratedUrls("TRT FM", ""));
        Assert.Empty(RadioLogoCache.FindCuratedUrls("Radyo Dost", ""));
        Assert.Empty(RadioLogoCache.FindCuratedUrls("TRT Radyo 3", ""));
        Assert.False(RadioLogoCache.IsSafePublicUri(new Uri("http://127.0.0.1:8765/api/state")));
        Assert.False(RadioLogoCache.IsSafePublicUri(new Uri("http://192.168.1.10/logo.png")));
        Assert.False(RadioLogoCache.IsSafePublicUri(new Uri("http://localhost/logo.png")));
        Assert.True(RadioLogoCache.IsSafePublicUri(new Uri("https://cdn.radyofenomen.com/logo.png")));
        Assert.True(Lan.AllowedOverLan("/api/controls/radio-logo"));

        var cacheDir = Path.Combine(Path.GetTempPath(), "phonebox-logos-" + Guid.NewGuid().ToString("N"));
        var fakePng = new byte[48];
        new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }.CopyTo(fakePng, 0);
        var hits = 0;

        try
        {
            using var http = new HttpClient(new FakeHandler(req =>
            {
                Interlocked.Increment(ref hits);
                if (req.RequestUri!.Host.Contains("radyofenomen.com", StringComparison.OrdinalIgnoreCase))
                    return BytesResponse(req, fakePng, "image/png");
                return new HttpResponseMessage(HttpStatusCode.NotFound) { RequestMessage = req };
            }));
            using var cache = new RadioLogoCache(cacheDir, http);

            var fenomen = new DeckAction { Id = "r1", Label = "Radyo Fenomen", Kind = "radio", Target = "https://live.radyofenomen.com/fenomen/128/icecast.audio" };
            var first = await cache.GetAsync(fenomen);
            var second = await cache.GetAsync(fenomen);
            Assert.Equal("image/png", first.Mime);
            Assert.Equal(fakePng, first.Bytes);
            Assert.Equal(1, hits);
            Assert.Equal(first.Bytes, second.Bytes);

            var unknown = new DeckAction { Id = "r2", Label = "📻 Jazz", Kind = "radio", Target = "https://example.com/live.mp3" };
            var fallback = await cache.GetAsync(unknown);
            Assert.Equal("image/svg+xml", fallback.Mime);
            var svgText = Encoding.UTF8.GetString(fallback.Bytes);
            Assert.Contains("<svg", svgText);
            Assert.DoesNotContain('\uFFFD', svgText);
        }
        finally
        {
            if (Directory.Exists(cacheDir)) Directory.Delete(cacheDir, recursive: true);
        }
    }

    [Fact]
    public async Task TapToToggleStartsAndStopsRadioWithoutSeparateStopAction()
    {
        var dir = Path.Combine(Path.GetTempPath(), "phonebox-radio-toggle-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var cfgPath = Path.Combine(dir, "config.json");
            File.WriteAllText(cfgPath, """
                {
                  "controls": {
                    "actions": [
                      { "id": "r-1", "label": "Jazz FM", "kind": "radio", "target": "https://example.com/jazz.mp3", "value": 80 },
                      { "id": "r-2", "label": "Classic FM", "kind": "radio", "target": "https://example.com/classic.mp3", "value": 75 }
                    ]
                  }
                }
                """);

            var native = new StatefulRadioNative();
            using var deck = new ControlDeck(new ConfigStore(cfgPath), native, NullLogger<ControlDeck>.Instance);

            Assert.True((await deck.Act("action", "r-1", "toggle", null, null, null, default)).Ok);
            Assert.Equal(["r-1"], native.PlayingRadios.ToArray());

            // Empty op (e.g. from ApplyProfile) keeps playing instead of toggling off
            Assert.True((await deck.Act("action", "r-1", "", null, null, null, default)).Ok);
            Assert.Equal(["r-1"], native.PlayingRadios.ToArray());

            Assert.True((await deck.Act("action", "r-2", "toggle", null, null, null, default)).Ok);
            Assert.Equal(["r-2"], native.PlayingRadios.ToArray());

            Assert.True((await deck.Act("action", "r-2", "toggle", null, null, null, default)).Ok);
            Assert.Empty(native.PlayingRadios);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    private static HttpResponseMessage BytesResponse(HttpRequestMessage req, byte[] body, string mediaType)
    {
        var res = new HttpResponseMessage(HttpStatusCode.OK) { RequestMessage = req, Content = new ByteArrayContent(body) };
        res.Content.Headers.ContentType = new MediaTypeHeaderValue(mediaType);
        return res;
    }

    private sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }

    private sealed class StatefulRadioNative : IDeckNative
    {
        public readonly HashSet<string> PlayingRadios = [];
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
        public Task<IReadOnlyList<DeckMedia>> Media(CancellationToken ct) => Task.FromResult<IReadOnlyList<DeckMedia>>([]);
        public Task<bool> MediaAction(string id, string action, long value, CancellationToken ct) => Task.FromResult(true);
        public void Play(string id, string file, string? device, int volume) => PlayingRadios.Add(id);
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

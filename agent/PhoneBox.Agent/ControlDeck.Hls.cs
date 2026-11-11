using System.Net;
using System.Text;
#if WINDOWS
using Windows.Devices.Enumeration;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Media.Streaming.Adaptive;
#endif

namespace PhoneBox.Agent;

internal static class HlsPlaylist
{
    private static readonly HttpClient SharedHttp = CreateClient();

    private static HttpClient CreateClient()
    {
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            ConnectTimeout = TimeSpan.FromSeconds(5),
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        };
        var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(6) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("PhoneBox/1.0");
        return client;
    }

    public static bool IsHlsUrl(Uri uri) =>
        uri.Scheme is "http" or "https"
        && uri.AbsolutePath.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase);

    public static bool IsHlsContentType(string? mediaType) =>
        mediaType is not null
        && (mediaType.Equals("application/vnd.apple.mpegurl", StringComparison.OrdinalIgnoreCase)
            || mediaType.Equals("application/x-mpegurl", StringComparison.OrdinalIgnoreCase));

    public static bool LooksLikeHlsManifest(string text)
    {
        var trimmed = text.AsSpan().TrimStart('\uFEFF').TrimStart();
        return trimmed.StartsWith("#EXTM3U", StringComparison.Ordinal)
            && (trimmed.Contains("#EXT-X-TARGETDURATION:", StringComparison.Ordinal)
                || trimmed.Contains("#EXT-X-STREAM-INF:", StringComparison.Ordinal));
    }

    /// <summary>
    /// Probes an HTTP(S) URI with a bounded 512-byte prefix read to check if it serves an HLS manifest without blocking on infinite audio streams.
    /// </summary>
    public static bool ProbeManifest(Uri uri, HttpClient? client = null)
    {
        if (uri.Scheme is not ("http" or "https")) return false;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(4));
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            using var response = (client ?? SharedHttp).SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token).GetAwaiter().GetResult();
            if (!response.IsSuccessStatusCode) return false;
            using var stream = response.Content.ReadAsStream(cts.Token);
            Span<byte> prefix = stackalloc byte[512];
            var total = 0;
            while (total < prefix.Length)
            {
                var read = stream.Read(prefix[total..]);
                if (read <= 0) break;
                total += read;
            }
            var text = Encoding.UTF8.GetString(prefix[..total]);
            return LooksLikeHlsManifest(text)
                || (HlsPlaylist.IsHlsContentType(response.Content.Headers.ContentType?.MediaType)
                    && text.AsSpan().TrimStart('\uFEFF').TrimStart().StartsWith("#EXTM3U", StringComparison.Ordinal)
                    && text.Contains("#EXT-X-", StringComparison.Ordinal));
        }
        catch { return false; }
    }
}

#if WINDOWS
internal static class HlsPlayer
{
    public static IDisposable Open(Uri uri, string? deviceId, int volume, Action onStopped)
    {
        var created = AdaptiveMediaSource.CreateFromUriAsync(uri).AsTask().WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
        if (created.Status != AdaptiveMediaSourceCreationStatus.Success || created.MediaSource is null)
            throw new InvalidOperationException($"HLS adaptive source failed ({created.Status})");

        var adaptive = created.MediaSource;
        MediaSource? mediaSource = null;
        MediaPlayer? player = null;
        try
        {
            mediaSource = MediaSource.CreateFromAdaptiveMediaSource(adaptive);
            player = new MediaPlayer
            {
                AudioCategory = MediaPlayerAudioCategory.Media,
                Volume = Math.Clamp(volume, 0, 100) / 100.0,
            };
            player.CommandManager.IsEnabled = false;
            if (!string.IsNullOrWhiteSpace(deviceId))
            {
                try
                {
                    var dev = DeviceInformation.CreateFromIdAsync(deviceId).AsTask().WaitAsync(TimeSpan.FromSeconds(2)).GetAwaiter().GetResult();
                    if (dev is not null) player.AudioDevice = dev;
                }
                catch { }
            }
            player.Source = mediaSource;
            var handle = new Handle(player, mediaSource, adaptive, onStopped);
            player.MediaEnded += (_, _) => handle.NotifyStopped();
            player.MediaFailed += (_, _) => handle.NotifyStopped();
            player.Play();
            return handle;
        }
        catch
        {
            player?.Dispose();
            mediaSource?.Dispose();
            adaptive.Dispose();
            throw;
        }
    }

    private sealed class Handle(MediaPlayer player, MediaSource source, AdaptiveMediaSource adaptive, Action onStopped) : IDisposable
    {
        private int _disposed;

        public void NotifyStopped()
        {
            if ( Volatile.Read(ref _disposed) != 0) return;
            _ = Task.Run(onStopped);
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            try { player.Pause(); } catch { }
            try { player.Source = null; } catch { }
            try { player.Dispose(); } catch { }
            try { source.Dispose(); } catch { }
            try { adaptive.Dispose(); } catch { }
        }
    }
}
#endif

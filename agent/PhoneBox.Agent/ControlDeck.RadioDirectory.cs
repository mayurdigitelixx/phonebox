using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PhoneBox.Agent;

public sealed record RadioCountryInfo(string Code, string Name, int StationCount);

public sealed record RadioStationCandidate(string Id, string Label, string StreamUrl, string? LogoUrl, int ClickCount, int Votes);

public sealed record RadioCheckRequest(IReadOnlyList<RadioCheckItem> Stations);

public sealed record RadioCheckItem(string Id, string StreamUrl);

public sealed record RadioCheckResult(string Id, bool Ok, string Status);

internal static partial class RadioBrowserDirectory
{
    private const string ApiBase = "https://de1.api.radio-browser.info/json";
    private const int MaxRedirects = 5;

    private static HttpClient CreateClient()
    {
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            ConnectTimeout = TimeSpan.FromSeconds(5),
            AllowAutoRedirect = false,
            UseProxy = false,
            ConnectCallback = ConnectPublicEndpointAsync,
        };
        var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("PhoneBox/" + AppPaths.Version);
        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return http;
    }

    public static async Task<IReadOnlyList<RadioCountryInfo>> GetCountriesAsync(CancellationToken ct, HttpClient? client = null)
    {
        using var owned = client is null ? CreateClient() : null;
        var http = client ?? owned!;
        var json = await http.GetStringAsync($"{ApiBase}/countries?hidebroken=true", ct);
        return ParseCountries(json);
    }

    internal static IReadOnlyList<RadioCountryInfo> ParseCountries(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Array) return [];
        var map = new Dictionary<string, RadioCountryInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (var el in doc.RootElement.EnumerateArray())
        {
            var code = Str(el, "iso_3166_1")?.Trim().ToUpperInvariant();
            var name = Str(el, "name")?.Trim();
            var count = Int(el, "stationcount");
            if (string.IsNullOrEmpty(code) || code.Length != 2 || string.IsNullOrEmpty(name) || count <= 0) continue;
            if (!map.TryGetValue(code, out var prev) || count > prev.StationCount)
                map[code] = new RadioCountryInfo(code, name, count);
        }
        return map.Values.OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public static async Task<IReadOnlyList<RadioStationCandidate>> GetStationsByCountryAsync(
        string countryCode, int limit, CancellationToken ct, HttpClient? client = null)
    {
        var cc = (countryCode ?? "").Trim().ToUpperInvariant();
        if (cc.Length != 2 || !cc.All(char.IsLetter)) return [];
        var max = Math.Clamp(limit <= 0 ? 40 : limit, 1, 100);
        using var owned = client is null ? CreateClient() : null;
        var http = client ?? owned!;
        var url = $"{ApiBase}/stations/search?countrycode={Uri.EscapeDataString(cc)}&hidebroken=true&order=clickcount&reverse=true&limit={max * 2}";
        var json = await http.GetStringAsync(url, ct);
        return ParseStations(json, cc, max);
    }

    internal static IReadOnlyList<RadioStationCandidate> ParseStations(string json, string countryCode, int max)
    {
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Array) return [];
        var seenUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var results = new List<RadioStationCandidate>();
        var cc = countryCode.ToLowerInvariant();

        foreach (var el in doc.RootElement.EnumerateArray())
        {
            var rawName = Str(el, "name")?.Trim();
            var streamUrl = (Str(el, "url_resolved") ?? Str(el, "url"))?.Trim();
            if (string.IsNullOrEmpty(rawName) || string.IsNullOrEmpty(streamUrl)) continue;
            if (!Uri.TryCreate(streamUrl, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)) continue;
            if (!seenUrls.Add(streamUrl)) continue;
            var cleanName = CleanStationName(rawName);
            if (string.IsNullOrEmpty(cleanName) || !seenNames.Add(cleanName)) continue;

            var favicon = Str(el, "favicon")?.Trim();
            var logoUrl = !string.IsNullOrEmpty(favicon) && Uri.TryCreate(favicon, UriKind.Absolute, out var favUri)
                && (favUri.Scheme == Uri.UriSchemeHttp || favUri.Scheme == Uri.UriSchemeHttps) ? favicon : null;
            var clicks = Int(el, "clickcount");
            var votes = Int(el, "votes");
            var slug = Slugify(cleanName);
            var id = $"radio-{cc}-{slug}";
            results.Add(new RadioStationCandidate(id, cleanName, streamUrl, logoUrl, clicks, votes));
            if (results.Count >= max) break;
        }
        return results;
    }

    public static async Task<IReadOnlyList<RadioCheckResult>> CheckStreamsAsync(
        IReadOnlyList<RadioCheckItem> items, CancellationToken ct, HttpClient? client = null)
    {
        if (items is null || items.Count == 0) return [];
        using var owned = client is null ? CreateClient() : null;
        var http = client ?? owned!;
        using var gate = new SemaphoreSlim(6);
        var tasks = items.Take(100).Select(async item =>
        {
            await gate.WaitAsync(ct);
            try { return await ProbeSingleAsync(http, item, ct); }
            finally { gate.Release(); }
        });
        return await Task.WhenAll(tasks);
    }

    private static async Task<RadioCheckResult> ProbeSingleAsync(HttpClient http, RadioCheckItem item, CancellationToken ct)
    {
        if (!Uri.TryCreate(item.StreamUrl, UriKind.Absolute, out var uri) || !IsSafePublicUri(uri))
            return new RadioCheckResult(item.Id, false, "Invalid URL");
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(5));
            using var res = await SendPublicGetAsync(http, uri, cts.Token);
            if (res is null)
                return new RadioCheckResult(item.Id, false, "Invalid redirect");
            if (!res.IsSuccessStatusCode)
                return new RadioCheckResult(item.Id, false, $"HTTP {(int)res.StatusCode}");
            var ctHeader = res.Content.Headers.ContentType?.MediaType ?? "";
            if (ctHeader.Contains("text/html", StringComparison.OrdinalIgnoreCase))
                return new RadioCheckResult(item.Id, false, "HTML page (not a stream)");
            return new RadioCheckResult(item.Id, true, "Live");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new RadioCheckResult(item.Id, false, "Timed out");
        }
        catch (Exception ex)
        {
            return new RadioCheckResult(item.Id, false, ex.GetType().Name);
        }
    }

    private static async Task<HttpResponseMessage?> SendPublicGetAsync(HttpClient http, Uri uri, CancellationToken ct)
    {
        var current = uri;
        for (var hop = 0; hop <= MaxRedirects; hop++)
        {
            if (!IsSafePublicUri(current)) return null;
            using var req = new HttpRequestMessage(HttpMethod.Get, current);
            req.Headers.TryAddWithoutValidation("Icy-MetaData", "0");
            var res = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            if ((int)res.StatusCode is >= 300 and <= 399 && res.Headers.Location is { } loc)
            {
                res.Dispose();
                if (!Uri.TryCreate(current, loc, out var next) || !IsSafePublicUri(next)) return null;
                current = next;
                continue;
            }
            return res;
        }
        return null;
    }

    internal static bool IsSafePublicUri(Uri uri)
    {
        if (uri.Scheme is not ("http" or "https")) return false;
        var host = uri.DnsSafeHost;
        if (string.IsNullOrWhiteSpace(host) || host.Equals("localhost", StringComparison.OrdinalIgnoreCase) || host.EndsWith(".local", StringComparison.OrdinalIgnoreCase))
            return false;
        if (IPAddress.TryParse(host, out var ip))
            return IsPublicIpAddress(ip);
        return true;
    }

    private static bool IsPublicIpAddress(IPAddress ip)
    {
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        if (IPAddress.IsLoopback(ip) || ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.None) || ip.Equals(IPAddress.IPv6Any) || ip.Equals(IPAddress.IPv6None))
            return false;
        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = ip.GetAddressBytes();
            if (b[0] is 0 or 10 or 127) return false;
            if (b[0] == 100 && b[1] is >= 64 and <= 127) return false;
            if (b[0] == 169 && b[1] == 254) return false;
            if (b[0] == 172 && b[1] is >= 16 and <= 31) return false;
            if (b[0] == 192 && b[1] == 168) return false;
            if (b[0] >= 224) return false;
            return true;
        }
        if (ip.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || ip.IsIPv6Multicast) return false;
            var b = ip.GetAddressBytes();
            if ((b[0] & 0xFE) == 0xFC) return false;
            return true;
        }
        return false;
    }

    private static async ValueTask<Stream> ConnectPublicEndpointAsync(SocketsHttpConnectionContext context, CancellationToken ct)
    {
        var host = context.DnsEndPoint.Host;
        var addresses = IPAddress.TryParse(host, out var parsed)
            ? [parsed]
            : await Dns.GetHostAddressesAsync(host, ct);
        var allowed = addresses.Where(IsPublicIpAddress).ToArray();
        if (allowed.Length == 0)
            throw new InvalidOperationException("Blocked connection to non-public address");
        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(allowed, context.DnsEndPoint.Port, ct);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    internal static string CleanStationName(string name)
    {
        var s = MultiSpaceRegex().Replace(name.Trim(), " ");
        return s.Length > 48 ? s[..48].Trim() : s;
    }

    internal static string Slugify(string text)
    {
        var lower = text.Replace('İ', 'i').Replace('I', 'i').ToLowerInvariant()
            .Replace('ı', 'i').Replace('ğ', 'g').Replace('ü', 'u')
            .Replace('ş', 's').Replace('ö', 'o').Replace('ç', 'c');
        var slug = NonAlphaNumRegex().Replace(lower, "-").Trim('-');
        return string.IsNullOrEmpty(slug) ? "station" : (slug.Length > 28 ? slug[..28].Trim('-') : slug);
    }

    private static string? Str(JsonElement e, string key) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static int Int(JsonElement e, string key) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) ? n : 0;

    [GeneratedRegex(@"\s+")]
    private static partial Regex MultiSpaceRegex();

    [GeneratedRegex(@"[^a-z0-9]+")]
    private static partial Regex NonAlphaNumRegex();
}

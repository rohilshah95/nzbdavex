using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using Serilog;

namespace NzbWebDAV.Clients.Indexers;

public class EasynewsClient(string username, string password, string? userAgent = null, string? proxyUrl = null)
{
    private const int MaxResultsPerPage = 250;
    private const string DefaultUserAgent = "_userAgent";
    
    private static readonly ConcurrentDictionary<string, HttpClient> Clients = new();
    private static readonly HashSet<string> AllowedVideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mkv", ".mp4", ".m4v", ".avi", ".ts", ".mov", ".wmv", ".mpg", ".mpeg", ".flv", ".webm"
    };

    private readonly string _username = username;
    private readonly string _password = password;
    private readonly string _userAgent = userAgent ?? DefaultUserAgent;
    private readonly HttpClient _http = GetClient(proxyUrl);

    private static HttpClient GetClient(string? proxyUrl)
    {
        var key = NormalizeProxy(proxyUrl) ?? "";
        return Clients.GetOrAdd(key, k =>
        {
            var handler = new HttpClientHandler();
            if (k.Length > 0 && Uri.TryCreate(k, UriKind.Absolute, out var uri))
            {
                handler.Proxy = new WebProxy(uri) { BypassProxyOnLocal = false };
                handler.UseProxy = true;
            }
            else
            {
                handler.UseProxy = false;
            }
            var client = new HttpClient(handler, disposeHandler: true) { Timeout = TimeSpan.FromSeconds(30) };
            client.BaseAddress = new Uri("https://members.easynews.com");
            return client;
        });
    }

    private static string? NormalizeProxy(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var trimmed = raw.Trim();
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri)) return null;
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) return null;
        return uri.ToString();
    }

    private async Task<HttpResponseMessage> GetAsync(string url, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.UserAgent.ParseAdd("_userAgent");
        return await _http.SendAsync(req, ct).ConfigureAwait(false);
    }

    public async Task<bool> TestAsync(CancellationToken ct = default)
    {
        var url = BuildSearchUrl("dune");
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.UserAgent.ParseAdd("_userAgent");
        req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
            "Basic", Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes($"{_username}:{_password}")));
        
        var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        if (resp.StatusCode == HttpStatusCode.Unauthorized || resp.StatusCode == HttpStatusCode.Forbidden)
            return false;
        if (!resp.IsSuccessStatusCode) return false;
        
        var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        try
        {
            var doc = JsonDocument.Parse(body);
            return doc.RootElement.TryGetProperty("data", out var data) && data.GetArrayLength() > 0;
        }
        catch
        {
            return false;
        }
    }

    public async Task<List<EasynewsItem>> SearchAsync(string query, int limit, CancellationToken ct = default)
    {
        var url = BuildSearchUrl(query, limit);
        // Full URL for debugging
        var fullUrl = $"https://members.easynews.com{url}";
        Log.Warning("EASYNEWS CLIENT: calling API with fullUrl={Url}, query={Query}", fullUrl, query);
        using var req = new HttpRequestMessage(HttpMethod.Get, fullUrl);
        req.Headers.UserAgent.ParseAdd(_userAgent);
        req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
            "Basic", Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes($"{_username}:{_password}")));
        
        var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        
        var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        Log.Warning("EASYNEWS CLIENT: response status={Status}, body length={Length}", resp.StatusCode, body.Length);
        
        var items = ParseResults(body);
        Log.Warning("EASYNEWS CLIENT: parsed {Count} items", items.Count);
        foreach (var item in items.Take(5))
        {
            Log.Warning("EASYNEWS RESULT: {Title}, size={Size}", item.Title, item.Size);
        }
        
        return items;
    }

    private static List<EasynewsItem> ParseResults(string body)
    {
        var items = new List<EasynewsItem>();
        if (string.IsNullOrEmpty(body)) return items;
        
        try
        {
            var doc = JsonDocument.Parse(body);
            
            // Check for API error
            if (doc.RootElement.TryGetProperty("error", out var errorElement))
            {
                var errorMsg = errorElement.GetString();
                throw new Exception(errorMsg ?? "Easynews API error");
            }
            
            if (!doc.RootElement.TryGetProperty("data", out var dataElement))
            {
                Log.Warning("EASYNEWS CLIENT: no 'data' property found");
                return items;
            }
            
            Log.Warning("EASYNEWS CLIENT: found data element with {Count} entries", dataElement.GetArrayLength());
            
            foreach (var entry in dataElement.EnumerateArray())
            {
                var item = ParseEntry(entry);
                if (item == null) continue;
                
                // Filter: skip samples, non-video files
                if (item.Title?.Contains("sample", StringComparison.OrdinalIgnoreCase) == true)
                    continue;
                
                // Only allow video extensions
                var ext = item.Extension ?? "";
                if (!string.IsNullOrEmpty(ext) && !AllowedVideoExtensions.Contains(ext))
                    continue;
                
                // Skip very short duration (< 60 seconds)
                if (item.DurationSeconds.HasValue && item.DurationSeconds.Value < 60)
                    continue;
                
                items.Add(item);
            }
        }
        catch (Exception ex)
        {
            Log.Warning("EASYNEWS CLIENT: parse error: {Message}", ex.Message);
        }
        
        return items;
    }

    public async Task<byte[]> DownloadNzbAsync(string hash, string filename, string ext, string sig, CancellationToken ct = default)
    {
        var formData = new Dictionary<string, string>
        {
            ["autoNZB"] = "1",
        };
        
        var key = string.IsNullOrEmpty(sig) ? "0" : $"0&sig={sig}";
        var fnB64 = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(filename ?? "")).TrimEnd('=');
        var extB64 = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(ext ?? "")).TrimEnd('=');
        formData[key] = $"{hash}|{fnB64}:{extB64}";
        
        if (!string.IsNullOrEmpty(filename))
            formData["nameZipQ0"] = filename;
        
        var content = new FormUrlEncodedContent(formData);
        
        using var req = new HttpRequestMessage(HttpMethod.Post, "/2.0/api/dl-nzb");
        req.Headers.UserAgent.ParseAdd("_userAgent");
        req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
            "Basic", Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes($"{_username}:{_password}")));
        req.Content = content;
        
        var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        
        return await resp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
    }

    private static string BuildSearchUrl(string query, int limit = 250)
    {
        // gps must come FIRST - API is order-sensitive
        var queryParams = new List<string>
        {
            $"gps={Uri.EscapeDataString(query)}",
            "fly=2",
            "sb=1",
            "pno=1",
            $"pby={limit}",
            "u=1",
        };
        
        return $"/2.0/search/solr-search/?{string.Join("&", queryParams)}";
    }

    private static EasynewsItem? ParseEntry(JsonElement entry)
    {
        try
        {
            // Handle both array format and object format
            string? hash = null, subject = null, filename = null, ext = null, poster = null, sig = null;
            long size = 0;
            double? duration = null;
            long? timestamp = null;
            
            if (entry.ValueKind == JsonValueKind.Array)
            {
                if (entry.GetArrayLength() < 12) return null;
                hash = entry[0].GetString();
                subject = entry[6].GetString();
                filename = entry[10].GetString();
                ext = entry[11].GetString();
                poster = entry[7].GetString();
                if (entry.GetArrayLength() > 12)
                    size = entry[12].TryGetInt64(out var arraySize) ? arraySize : 0;
                if (entry.GetArrayLength() > 14)
                    duration = entry[14].TryGetDouble(out var arrayDur) ? arrayDur : null;
            }
            else
            {
                hash = entry.TryGetProperty("hash", out var h) ? h.GetString() : null;
                subject = entry.TryGetProperty("subject", out var subj) ? subj.GetString() : null;
                filename = entry.TryGetProperty("fn", out var fn) ? fn.GetString() 
                    : entry.TryGetProperty("filename", out var f) ? f.GetString() : null;
                ext = entry.TryGetProperty("extension", out var e) ? e.GetString() 
                    : entry.TryGetProperty("ext", out var ex) ? ex.GetString() : null;
                poster = entry.TryGetProperty("poster", out var p) ? p.GetString() : null;
                sig = entry.TryGetProperty("sig", out var sg) ? sg.GetString() : null;
                size = entry.TryGetProperty("size", out var sz) && sz.TryGetInt64(out var objSize) ? objSize : 0;
                duration = entry.TryGetProperty("runtime", out var rt) && rt.TryGetDouble(out var objDur) ? objDur : null;
                timestamp = entry.TryGetProperty("timestamp", out var ts) && ts.TryGetInt64(out var objTs) ? objTs : null;
            }
            
            if (string.IsNullOrEmpty(hash)) return null;
            
            var title = filename ?? subject ?? hash;
            if (!string.IsNullOrEmpty(ext) && !title.EndsWith(ext, StringComparison.OrdinalIgnoreCase))
                title += ext;
            
            // Parse timestamp to date
            DateTimeOffset? posted = null;
            if (timestamp.HasValue)
                posted = DateTimeOffset.FromUnixTimeSeconds(timestamp.Value);
            
            int? durationSec = null;
            if (duration.HasValue && duration.Value > 0)
                durationSec = (int)duration.Value;
            
            return new EasynewsItem
            {
                Title = title ?? hash,
                Hash = hash,
                Filename = filename,
                Extension = ext,
                Size = size,
                Posted = posted,
                DurationSeconds = durationSec,
                Sig = sig,
            };
        }
        catch
        {
            return null;
        }
    }

    public class EasynewsItem
    {
        public required string Title { get; init; }
        public required string Hash { get; init; }
        public string? Filename { get; init; }
        public string? Extension { get; init; }
        public long Size { get; init; }
        public DateTimeOffset? Posted { get; init; }
        public int? DurationSeconds { get; init; }
        public string? Sig { get; init; }
        
        // For NZB download URL construction
        public string GetDownloadUrl(string baseUrl, string? token = null)
        {
            var payload = new Dictionary<string, string?>
            {
                ["hash"] = Hash,
                ["filename"] = Filename,
                ["ext"] = Extension,
                ["sig"] = Sig,
                ["title"] = Title,
            };
            
            var json = JsonSerializer.Serialize(payload);
            var encoded = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(json))
                .TrimEnd('=')
                .Replace('+', '-')
                .Replace('/', '_');
            
            var tokenSegment = !string.IsNullOrEmpty(token) ? $"/{token}" : "";
            return $"{baseUrl}{tokenSegment}/easynews/nzb?payload={Uri.EscapeDataString(encoded)}";
        }
    }
}
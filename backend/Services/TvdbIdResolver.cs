using System.Text.Json;

namespace NzbWebDAV.Services;

public class TvdbIdResolver
{
    private static readonly HttpClient HttpClient = new() { Timeout = TimeSpan.FromSeconds(10) };

    public async Task<int?> GetTvdbIdAsync(string imdbDigits, CancellationToken ct)
    {
        var info = await GetShowInfoAsync(imdbDigits, ct).ConfigureAwait(false);
        return info?.TvdbId;
    }

    public async Task<ShowInfo?> GetShowInfoAsync(string imdbDigits, CancellationToken ct)
    {
        // Try tvmaze first for TV shows
        var info = await TryTvmazeShowInfoAsync(imdbDigits, ct).ConfigureAwait(false);
        if (info != null) return info;
        
        // Fall back to simple IMDB lookup via search
        return await TrySimpleSearchAsync(imdbDigits, ct).ConfigureAwait(false);
    }

    private async Task<ShowInfo?> TrySimpleSearchAsync(string imdbDigits, CancellationToken ct)
    {
        try
        {
            // Try OMDB API - 1000 free requests/day
            var url = $"https://www.omdbapi.com/?i=tt{imdbDigits}&apikey=4a3b711b";
            using var resp = await HttpClient.GetAsync(url, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return null;
            await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
            
            // Check if response is valid
            if (!doc.RootElement.TryGetProperty("Response", out var response) || 
                response.GetString()?.ToLower() != "true")
                return null;
            
            var title = doc.RootElement.GetProperty("Title").GetString();
            var yearStr = doc.RootElement.GetProperty("Year").GetString();
            int? year = null;
            if (!string.IsNullOrEmpty(yearStr) && yearStr.Length >= 4 && int.TryParse(yearStr[..4], out var y))
                year = y;
            
            return new ShowInfo { Name = title ?? "", Year = year };
        }
        catch
        {
            return null;
        }
    }

    private async Task<int?> TryTvmazeAsync(string imdbDigits, CancellationToken ct)
    {
        try
        {
            var url = $"https://api.tvmaze.com/lookup/shows?imdb=tt{imdbDigits}";
            using var resp = await HttpClient.GetAsync(url, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return null;
            await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;
            if (!doc.RootElement.TryGetProperty("externals", out var externals)) return null;
            if (!externals.TryGetProperty("thetvdb", out var tvdbElement)) return null;
            if (tvdbElement.ValueKind == JsonValueKind.Number && tvdbElement.TryGetInt32(out var tvdbId))
                return tvdbId;
            return null;
        }
        catch
        {
            return null;
        }
    }

    private async Task<int?> TryWikidataAsync(string imdbDigits, CancellationToken ct)
    {
        try
        {
            var query = $"SELECT ?tvdb WHERE {{ ?item wdt:P345 \"tt{imdbDigits}\" . ?item wdt:P4835 ?tvdb . }} LIMIT 1";
            var url = $"https://query.wikidata.org/sparql?query={Uri.EscapeDataString(query)}";
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Accept.ParseAdd("application/sparql-results+json");
            req.Headers.UserAgent.ParseAdd("NzbDav (https://github.com/nzbdav-dev/nzbdav)");
            using var resp = await HttpClient.SendAsync(req, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return null;
            await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
            var bindings = doc.RootElement.GetProperty("results").GetProperty("bindings");
            if (bindings.GetArrayLength() == 0) return null;
            var tvdbStr = bindings[0].GetProperty("tvdb").GetProperty("value").GetString();
            return int.TryParse(tvdbStr, out var tvdbId) ? tvdbId : null;
        }
        catch
        {
            return null;
        }
    }

    private async Task<ShowInfo?> TryTvmazeShowInfoAsync(string imdbDigits, CancellationToken ct)
    {
        try
        {
            var url = $"https://api.tvmaze.com/lookup/shows?imdb=tt{imdbDigits}";
            using var resp = await HttpClient.GetAsync(url, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return null;
            await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;
            
            var name = doc.RootElement.GetProperty("name").GetString();
            if (string.IsNullOrEmpty(name)) return null;
            
            var externals = doc.RootElement.GetProperty("externals");
            int? tvdbId = null;
            if (externals.TryGetProperty("thetvdb", out var tvdbElement) && tvdbElement.ValueKind == JsonValueKind.Number)
                tvdbId = tvdbElement.GetInt32();
            
            int? year = null;
            if (doc.RootElement.TryGetProperty("premiered", out var premiered) && premiered.ValueKind == JsonValueKind.String)
            {
                var premieredStr = premiered.GetString()![..4];
                int.TryParse(premieredStr, out var y);
                year = y;
            }
            
            return new ShowInfo { Name = name, TvdbId = tvdbId, Year = year };
        }
        catch
        {
            return null;
        }
    }
}

public class ShowInfo
{
    public string Name { get; set; } = "";
    public int? TvdbId { get; set; }
    public int? Year { get; set; }
}

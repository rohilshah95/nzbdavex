using Microsoft.AspNetCore.Mvc;
using NzbWebDAV.Clients.Indexers;
using NzbWebDAV.Config;
using NzbWebDAV.Extensions;
using NzbWebDAV.Services;
using NzbWebDAV.Utils;
using Serilog;
using IndexerResultFilter = NzbWebDAV.Services.IndexerResultFilter;
using NewznabItem = NzbWebDAV.Clients.Indexers.NewznabClient.NewznabItem;
using SearchResultType = System.Collections.Generic.IEnumerable<dynamic>;

namespace NzbWebDAV.Api.Controllers.Profiles;

[ApiController]
[Route("p/{token}/stream/{type}/{id}.json")]
public class ProfileReadController(
    ConfigManager configManager,
    NzbResolutionCache cache,
    NewznabRateLimiter rateLimiter,
    TvdbIdResolver tvdbResolver,
    PreflightOrchestrator preflightOrchestrator
) : ControllerBase
{
    [HttpOptions]
    public IActionResult Preflight()
    {
        ProfileManifestController.SetCors(Response);
        return NoContent();
    }

    [HttpGet]
    public async Task<IActionResult> Get(string token, string type, string id)
    {
        ProfileManifestController.SetCors(Response);

        var profile = configManager.GetProfileConfig().Profiles.FirstOrDefault(x => x.Token == token);
        if (profile is null) return NotFound();

        var indexerConfig = configManager.GetIndexerConfig();
        var allIndexers = indexerConfig.Indexers.Where(x => x.Enabled).ToList();
        var indexers = profile.IndexerNames.Count == 0
            ? allIndexers
            : allIndexers.Where(x => profile.IndexerNames.Contains(x.Name)).ToList();
        var globalProxy = indexerConfig.ProxyUrl;

        if (indexers.Count == 0) return new JsonResult(new { streams = Array.Empty<object>() });

        var ct = HttpContext.RequestAborted;
        var queryParams = await BuildQueryAsync(type, id, ct).ConfigureAwait(false);
        if (queryParams is null) return new JsonResult(new { streams = Array.Empty<object>() });

        var now = DateTimeOffset.UtcNow;
        var easynewsToken = configManager.GetEasynewsToken();
        var easynewsBaseUrl = HttpContext.GetPublicBaseUrl(configManager.GetBaseUrl());
        
        var gpsQuery = queryParams != null ? BuildEasynewsSearchQuery(queryParams) : "(no params)";
        Log.Warning("EASYNEWS DEBUG: Profile search hit - type={Type} id={Id}, gps={GPS}, params={Params}", 
            type, id, gpsQuery, queryParams != null ? string.Join(",", queryParams.Select(kv => $"{kv.Key}={kv.Value}")) : "none");
        
        Func<IndexerConfig.ConnectionDetails, Task<IEnumerable<dynamic>>> searchIndex = async x =>
        {
            var ua = string.IsNullOrWhiteSpace(x.UserAgent) ? configManager.GetUserAgent() : x.UserAgent;
            var proxy = string.IsNullOrWhiteSpace(x.ProxyUrl) ? globalProxy : x.ProxyUrl;
            await rateLimiter.WaitAsync(x.Name, x.MaxRequestsPerMinute, ct).ConfigureAwait(false);
            
            // Easynews
            if (x.IndexerType?.ToLowerInvariant() == "easynews")
            {
                var username = x.Username ?? "";
                var password = x.ApiKey;
                if (string.IsNullOrEmpty(username) || string.IsNullOrEmpty(password))
                {
                    Log.Warning("Indexer {Indexer} requires username and password", x.Name);
                    return Enumerable.Empty<dynamic>();
                }
                
                // Build search query from Newznab params
                var searchQuery = BuildEasynewsSearchQuery(queryParams);
                Log.Information("Easynews search for {Indexer}: query={Query}, params={Params}", x.Name, searchQuery, string.Join(",", queryParams.Select(kv => $"{kv.Key}={kv.Value}")));
                if (string.IsNullOrEmpty(searchQuery))
                {
                    Log.Warning("Easynews search: empty query, params: {Params}", string.Join(",", queryParams.Select(kv => $"{kv.Key}={kv.Value}")));
                    return Enumerable.Empty<dynamic>();
                }
                
                Log.Information("Easynews search query: {Query}", searchQuery);
                var client = new EasynewsClient(username, password, ua, proxy);
                var items = await client.SearchAsync(searchQuery, 50, ct).ConfigureAwait(false);
                return items.Select(i => new { 
                    indexer = x.Name, 
                    userAgent = ua, 
                    item = new NewznabItem { 
                        Title = i.Title, 
                        NzbUrl = i.GetDownloadUrl(easynewsBaseUrl, easynewsToken),
                        Size = i.Size, 
                        Posted = i.Posted,
                        Guid = i.Hash ?? i.Title
                    } 
                });
            }
            
            // Newznab
            var client2 = new NewznabClient(x.Url, x.ApiKey, ua, proxy);
            var items2 = await client2.QueryAsync(queryParams, ct).ConfigureAwait(false);
            var filtered = IndexerResultFilter.Apply(items2, x.Filter, now);
            return filtered.Select(i => new { indexer = x.Name, userAgent = ua, item = i });
        };
        
        var perIndexer = await Task.WhenAll(indexers.Select(async x =>
        {
            try { return await searchIndex(x).ConfigureAwait(false); }
            catch (Exception e) { if (!e.IsCancellationException()) Log.Warning("Indexer {Indexer} search failed: {Message}", x.Name, e.Message); return Enumerable.Empty<dynamic>(); }
        })).ConfigureAwait(false);

        var anyPreferDownloaded = indexers.Any(x => x.Filter is { Enabled: true, PreferDownloaded: true });

        var baseUrl = HttpContext.GetPublicBaseUrl(configManager.GetBaseUrl());
        var dedupedQuery = perIndexer
            .SelectMany(x => x)
            .Where(x => !string.IsNullOrWhiteSpace(x.item.NzbUrl))
            .GroupBy(x => x.item.NzbUrl)
            .Select(g => g.First());

        // When any indexer opts into PreferDownloaded, grabs becomes the primary sort key
        // for the merged list (items missing grabs go below honest 0-grab items). Existing
        // size/date ordering stays as the tiebreaker — and remains the only ordering when
        // no indexer requests grab-based ranking.
        var deduped = (anyPreferDownloaded
                ? dedupedQuery.OrderByDescending(x => x.item.Grabs ?? -1)
                              .ThenByDescending(x => x.item.Size)
                              .ThenByDescending(x => x.item.Posted ?? DateTimeOffset.MinValue)
                : dedupedQuery.OrderByDescending(x => x.item.Size)
                              .ThenByDescending(x => x.item.Posted ?? DateTimeOffset.MinValue))
            .ToList();

        var strictIndexers = indexers
            .Where(x => x.EnableStrictMatching)
            .Select(x => x.Name)
            .ToHashSet();

        if (strictIndexers.Count > 0 && deduped.Count >= 2)
        {
            var withHead = deduped
                .Select(x => new { Entry = x, Head = FilenameMatcher.HeadTokens(x.item.Title) })
                .ToList();

            var consensus = withHead
                .Where(x => x.Head.Length > 0)
                .GroupBy(x => string.Join(' ', x.Head))
                .Select(g => new { g.First().Head, Count = g.Count() })
                .OrderByDescending(x => x.Count)
                .FirstOrDefault();

            if (consensus is { Count: >= 2 })
            {
                deduped = withHead
                    .Where(x => !strictIndexers.Contains(x.Entry.indexer)
                                || FilenameMatcher.TokensEqual(x.Head, consensus.Head))
                    .Select(x => x.Entry)
                    .ToList();
            }
        }

        if (deduped.Count == 0) return new JsonResult(new { streams = Array.Empty<object>() });

        var candidates = deduped
            .Select(x => new NzbResolutionCache.Candidate
            {
                IndexerName = x.indexer,
                IndexerUserAgent = x.userAgent,
                NzbUrl = x.item.NzbUrl,
                Title = x.item.Title,
                Size = x.item.Size,
                Posted = x.item.Posted,
                UsenetDate = x.item.UsenetDate,
                Grabs = x.item.Grabs,
                Password = x.item.Password,
            })
            .ToList();

        var tokens = cache.AddGroup(candidates, type, token, id);

        preflightOrchestrator.Start(token, type, id, candidates);

        var streams = candidates
            .Select((c, i) =>
            {
                var description = BuildDescription(c);
                return new
                {
                    name = $"[NZB] {c.IndexerName}",
                    description,
                    title = description,
                    url = $"{baseUrl}/p/{token}/play/{tokens[i]}.mkv",
                    behaviorHints = new
                    {
                        filename = c.Title,
                        videoSize = c.Size,
                        bingeGroup = $"nzbdavex|{c.IndexerName}|{type}",
                        notWebReady = true,
                    },
                };
            })
            .ToList();

        return new JsonResult(new { streams });
    }

    private async Task<IReadOnlyDictionary<string, string>?> BuildQueryAsync(string type, string id, CancellationToken ct)
    {
        if (type == "movie")
        {
            var imdb = StripImdbPrefix(id);
            if (imdb is null) return null;
            
            // Get show info for movie name
            var showInfo = await tvdbResolver.GetShowInfoAsync(imdb, ct).ConfigureAwait(false);
            Log.Warning("EASYNEWS DEBUG: movie showInfo for {IMDB} = {Name}", imdb, showInfo?.Name);
            return new Dictionary<string, string>
            {
                ["t"] = "movie",
                ["imdbid"] = imdb,
                ["cat"] = "2000",
                ["limit"] = "200",
                ["search"] = showInfo != null ? $"{showInfo.Name} {showInfo.Year}" : imdb,
            };
        }
        if (type == "series")
        {
            var parts = id.Split(':');
            if (parts.Length < 3) return null;
            var imdb = StripImdbPrefix(parts[0]);
            if (imdb is null) return null;
            if (!int.TryParse(parts[1], out var season)) return null;
            if (!int.TryParse(parts[2], out var episode)) return null;
            
            // Get show info for show name
            var showInfo = await tvdbResolver.GetShowInfoAsync(imdb, ct).ConfigureAwait(false);
            Log.Warning("EASYNEWS DEBUG: showInfo for {IMDB} = {Name}, tvdb={Tvdb}", imdb, showInfo?.Name, showInfo?.TvdbId);
            var showName = showInfo?.Name ?? "";
            var dict = new Dictionary<string, string>
            {
                ["t"] = "tvsearch",
                ["season"] = season.ToString(),
                ["ep"] = episode.ToString(),
                ["cat"] = "5000",
                ["limit"] = "200",
                ["search"] = !string.IsNullOrEmpty(showName) ? $"{showName} S{int.Parse(parts[1]):00}E{int.Parse(parts[2]):00}" : imdb,
            };
            var tvdb = showInfo?.TvdbId;
            if (tvdb.HasValue) dict["tvdbid"] = tvdb.Value.ToString();
            else dict["imdbid"] = imdb;
            return dict;
        }
        return null;
    }

    private static string? StripImdbPrefix(string id)
    {
        if (!id.StartsWith("tt", StringComparison.OrdinalIgnoreCase)) return null;
        var digits = id[2..];
        return digits.All(char.IsDigit) ? digits : null;
    }

    private static string BuildEasynewsSearchQuery(IReadOnlyDictionary<string, string> queryParams)
    {
        // Handle text search
        if (queryParams.TryGetValue("q", out var q) && !string.IsNullOrEmpty(q))
            return q;
        
        // Check for pre-built search string with show name FIRST (e.g., "Gen V S01E01")
        if (queryParams.TryGetValue("search", out var searchStr) && !string.IsNullOrEmpty(searchStr))
            return searchStr;
        
        // Handle movie search fallback
        if (queryParams.TryGetValue("t", out var t) && t == "movie")
        {
            // Fallback: just use the title if we have it, or try imdb search
            if (queryParams.TryGetValue("imdbid", out var imdb))
                return $"imdb {imdb}"; // Try "imdb 0463854" format
            return "";
        }
        
        // Handle TV search fallback
        if (t == "tvsearch")
        {
            // Fallback: try tvdb search
            if (queryParams.TryGetValue("tvdbid", out var tvdb) && !string.IsNullOrEmpty(tvdb))
            {
                var season = queryParams.GetValueOrDefault("season");
                var ep = queryParams.GetValueOrDefault("ep");
                if (!string.IsNullOrEmpty(season) && !string.IsNullOrEmpty(ep))
                    return $"tvdb {tvdb} S{int.Parse(season):00}E{int.Parse(ep):00}";
                return $"tvdb {tvdb}";
            }
            if (queryParams.TryGetValue("imdbid", out var imdb) && !string.IsNullOrEmpty(imdb))
            {
                var season = queryParams.GetValueOrDefault("season");
                var ep = queryParams.GetValueOrDefault("ep");
                if (!string.IsNullOrEmpty(season) && !string.IsNullOrEmpty(ep))
                    return $"imdb {imdb} S{int.Parse(season):00}E{int.Parse(ep):00}";
                return $"imdb {imdb}";
            }
            return "";
        }
        
        return "";
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes <= 0) return "?";
        string[] s = ["B", "KB", "MB", "GB", "TB"];
        var i = 0;
        double v = bytes;
        while (v >= 1024 && i < s.Length - 1) { v /= 1024; i++; }
        return $"{v:0.##} {s[i]}";
    }

    private static string BuildDescription(NzbResolutionCache.Candidate c)
    {
        var meta = new List<string> { $"💾 {FormatBytes(c.Size)}" };
        if (c.Posted is { } p) meta.Add($"📅 {FormatAge(DateTimeOffset.UtcNow - p)}");
        meta.Add($"🌐 {c.IndexerName}");
        return $"{c.Title}\n{string.Join(" | ", meta)}";
    }

    private static string FormatAge(TimeSpan a)
    {
        if (a.TotalDays >= 365) return $"{(int)(a.TotalDays / 365)}y";
        if (a.TotalDays >= 1) return $"{(int)a.TotalDays}d";
        if (a.TotalHours >= 1) return $"{(int)a.TotalHours}h";
        return $"{Math.Max(1, (int)a.TotalMinutes)}m";
    }
}

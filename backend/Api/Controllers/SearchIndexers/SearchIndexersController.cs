using System.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using NzbWebDAV.Clients.Indexers;
using NzbWebDAV.Config;
using NzbWebDAV.Services;
using NzbWebDAV.Utils;


namespace NzbWebDAV.Api.Controllers.SearchIndexers;

[ApiController]
[Route("api/search-indexers")]
public class SearchIndexersController(ConfigManager configManager, NewznabRateLimiter rateLimiter) : BaseApiController
{
    protected override async Task<IActionResult> HandleRequest()
    {
        var request = new SearchIndexersRequest(HttpContext);
        var indexerConfig = configManager.GetIndexerConfig();
        var indexers = indexerConfig.Indexers.Where(x => x.Enabled).ToList();
        var globalProxy = indexerConfig.ProxyUrl;
        var ct = HttpContext.RequestAborted;

        var perIndexer = await Task.WhenAll(indexers.Select(async x =>
        {
            var sw = Stopwatch.StartNew();
            try
            {
                await rateLimiter.WaitAsync(x.Name, x.MaxRequestsPerMinute, ct).ConfigureAwait(false);
                
                // Determine indexer type (default to newznab)
                var indexerType = x.IndexerType?.ToLowerInvariant() ?? "newznab";
                
                List<SearchIndexersResponse.Result> mapped;
                
                if (indexerType == "easynews")
                {
                    // Easynews indexer
                    var username = x.Username ?? "";
                    var password = x.ApiKey; // Use ApiKey as password for easynews
                    
                    if (string.IsNullOrEmpty(username) || string.IsNullOrEmpty(password))
                    {
                        throw new Exception("Easynews requires username and password");
                    }
                    
                    var proxy = string.IsNullOrWhiteSpace(x.ProxyUrl) ? globalProxy : x.ProxyUrl;
                    var ua = string.IsNullOrWhiteSpace(x.UserAgent) ? configManager.GetUserAgent() : x.UserAgent;
                    var client = new EasynewsClient(username, password, ua, proxy);
                    var items = await client.SearchAsync(request.Query, request.Limit, ct).ConfigureAwait(false);
                    
                    // Get base URL and optional token for download URLs
                    var baseUrl = configManager.GetBaseUrl();
                    var easynewsToken = configManager.GetEasynewsToken();
                    
                    mapped = items
                        .Where(i => !x.EnableStrictMatching || FilenameMatcher.Matches(request.Query, i.Title))
                        .Select(i => new SearchIndexersResponse.Result
                        {
                            Indexer = x.Name,
                            Title = i.Title,
                            NzbUrl = i.GetDownloadUrl(baseUrl, easynewsToken),
                            Size = i.Size,
                            Posted = i.Posted,
                        }).ToList();
                }
                else
                {
                    // Newznab indexer (default)
                    var ua = string.IsNullOrWhiteSpace(x.UserAgent) ? configManager.GetUserAgent() : x.UserAgent;
                    var proxy = string.IsNullOrWhiteSpace(x.ProxyUrl) ? globalProxy : x.ProxyUrl;
                    var client = new NewznabClient(x.Url, x.ApiKey, ua, proxy);
                    var items = await client.SearchAsync(request.Query, request.Limit, ct).ConfigureAwait(false);
                    
                    mapped = items
                        .Where(i => !x.EnableStrictMatching || FilenameMatcher.Matches(request.Query, i.Title))
                        .Select(i => new SearchIndexersResponse.Result
                        {
                            Indexer = x.Name,
                            Title = i.Title,
                            NzbUrl = i.NzbUrl,
                            Size = i.Size,
                            Posted = i.Posted,
                        }).ToList();
                }
                
                return (Status: new SearchIndexersResponse.IndexerStatus
                {
                    Name = x.Name,
                    Ok = true,
                    ResultCount = mapped.Count,
                    ElapsedMs = sw.ElapsedMilliseconds,
                }, Results: mapped);
            }
            catch (Exception e)
            {
                return (Status: new SearchIndexersResponse.IndexerStatus
                {
                    Name = x.Name,
                    Ok = false,
                    Error = e.Message,
                    ElapsedMs = sw.ElapsedMilliseconds,
                }, Results: new List<SearchIndexersResponse.Result>());
            }
        }).ToList()).ConfigureAwait(false);

        return Ok(new SearchIndexersResponse
        {
            Results = perIndexer.SelectMany(x => x.Results)
                .GroupBy(x => x.NzbUrl)
                .Select(g => g.First())
                .OrderByDescending(x => x.Posted ?? DateTimeOffset.MinValue)
                .ToList(),
            Indexers = perIndexer.Select(x => x.Status).ToList(),
        });
    }
}

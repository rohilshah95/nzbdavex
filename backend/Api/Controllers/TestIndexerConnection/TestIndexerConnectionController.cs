using Microsoft.AspNetCore.Mvc;
using NzbWebDAV.Clients.Indexers;

namespace NzbWebDAV.Api.Controllers.TestIndexerConnection;

[ApiController]
[Route("api/test-indexer-connection")]
public class TestIndexerConnectionController(NzbWebDAV.Config.ConfigManager configManager) : BaseApiController
{
    protected override async Task<IActionResult> HandleRequest()
    {
        var request = new TestIndexerConnectionRequest(HttpContext);
        try
        {
            var indexerType = request.IndexerType?.ToLowerInvariant() ?? "newznab";
            bool ok;
            
            if (indexerType == "easynews")
            {
                // Easynews: username and password (ApiKey = password)
                var username = request.Username ?? "";
                var password = request.ApiKey;
                
                if (string.IsNullOrEmpty(username) || string.IsNullOrEmpty(password))
                {
                    return Ok(new TestIndexerConnectionResponse { Status = true, Connected = false, Error = "Easynews requires username and password" });
                }
                
                var proxy = string.IsNullOrWhiteSpace(request.ProxyUrl)
                    ? configManager.GetIndexerConfig().ProxyUrl
                    : request.ProxyUrl;
                var client = new EasynewsClient(username, password, proxy);
                ok = await client.TestAsync(HttpContext.RequestAborted).ConfigureAwait(false);
            }
            else
            {
                // Newznab (default)
                var ua = string.IsNullOrWhiteSpace(request.UserAgent) ? configManager.GetUserAgent() : request.UserAgent;
                var proxy = string.IsNullOrWhiteSpace(request.ProxyUrl)
                    ? configManager.GetIndexerConfig().ProxyUrl
                    : request.ProxyUrl;
                var client = new NewznabClient(request.Url, request.ApiKey, ua, proxy);
                ok = await client.TestAsync(HttpContext.RequestAborted).ConfigureAwait(false);
            }
            
            return Ok(new TestIndexerConnectionResponse { Status = true, Connected = ok });
        }
        catch (Exception e)
        {
            return Ok(new TestIndexerConnectionResponse { Status = true, Connected = false, Error = e.Message });
        }
    }
}

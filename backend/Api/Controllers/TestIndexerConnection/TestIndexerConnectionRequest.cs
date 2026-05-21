using Microsoft.AspNetCore.Http;

namespace NzbWebDAV.Api.Controllers.TestIndexerConnection;

public class TestIndexerConnectionRequest
{
    public string Url { get; init; }
    public string ApiKey { get; init; }
    public string? Username { get; init; }
    public string? UserAgent { get; init; }
    public string? ProxyUrl { get; init; }
    public string IndexerType { get; init; } = "newznab";

    public TestIndexerConnectionRequest(HttpContext context)
    {
        Url = context.Request.Form["url"].FirstOrDefault() ?? "";
        ApiKey = context.Request.Form["apiKey"].FirstOrDefault() ?? "";
        Username = context.Request.Form["username"].FirstOrDefault();
        UserAgent = context.Request.Form["userAgent"].FirstOrDefault();
        ProxyUrl = context.Request.Form["proxyUrl"].FirstOrDefault();
        IndexerType = context.Request.Form["indexerType"].FirstOrDefault() ?? "newznab";
    }
}

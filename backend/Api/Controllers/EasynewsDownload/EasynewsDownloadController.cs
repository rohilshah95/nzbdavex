using System.Text;
using System.Web;
using Microsoft.AspNetCore.Mvc;
using NzbWebDAV.Clients.Indexers;
using NzbWebDAV.Config;

namespace NzbWebDAV.Api.Controllers.EasynewsDownload;

[ApiController]
[Route("easynews")]
public class EasynewsDownloadController : ControllerBase
{
    private readonly ConfigManager _configManager;
    
    public EasynewsDownloadController(ConfigManager configManager)
    {
        _configManager = configManager;
    }
    
    [HttpGet("nzb")]
    public async Task<IActionResult> DownloadNzb(CancellationToken ct)
    {
        // Get payload from query string
        var payload = Request.Query["payload"].FirstOrDefault();
        if (string.IsNullOrEmpty(payload))
        {
            return BadRequest("Missing payload parameter");
        }
        
        // Decode the payload
        Dictionary<string, string?>? payloadData;
        try
        {
            // Fix URL-safe base64 padding
            var normalized = payload.Replace('-', '+').Replace('_', '/');
            var padLength = (4 - (normalized.Length % 4)) % 4;
            var padded = normalized + new string('=', padLength);
            
            var json = Encoding.UTF8.GetString(Convert.FromBase64String(padded));
            payloadData = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string?>>(json);
        }
        catch (Exception ex)
        {
            return BadRequest($"Invalid payload: {ex.Message}");
        }
        
        if (payloadData == null || !payloadData.TryGetValue("hash", out var hash) || string.IsNullOrEmpty(hash))
        {
            return BadRequest("Invalid payload: missing hash");
        }
        
        var filename = payloadData.GetValueOrDefault("filename") ?? "download";
        var ext = payloadData.GetValueOrDefault("ext") ?? "";
        var sig = payloadData.GetValueOrDefault("sig");
        var title = payloadData.GetValueOrDefault("title") ?? filename;
        
        // Get easynews credentials from indexer config
        var indexerConfig = _configManager.GetIndexerConfig();
        var easynewsIndexer = indexerConfig.Indexers
            .FirstOrDefault(x => x.Enabled && x.IndexerType?.ToLowerInvariant() == "easynews");
        
        if (easynewsIndexer == null)
        {
            return NotFound("No easynews indexer configured");
        }
        
        var username = easynewsIndexer.Username ?? "";
        var password = easynewsIndexer.ApiKey; // Using ApiKey as password
        
        if (string.IsNullOrEmpty(username) || string.IsNullOrEmpty(password))
        {
            return Unauthorized("Easynews credentials not configured");
        }
        
        var proxy = string.IsNullOrWhiteSpace(easynewsIndexer.ProxyUrl) 
            ? indexerConfig.ProxyUrl 
            : easynewsIndexer.ProxyUrl;
        
        try
        {
            var client = new EasynewsClient(username, password, proxy);
            var nzbData = await client.DownloadNzbAsync(hash, filename, ext, sig ?? "", ct);
            
            // Ensure proper filename
            var safeName = title.Replace(" ", "_")
                .Replace("/", "_")
                .Replace("\\", "_")
                .Replace(":", "_")
                .Replace("*", "_")
                .Replace("?", "_")
                .Replace("\"", "_")
                .Replace("<", "_")
                .Replace(">", "_")
                .Replace("|", "_");
            
            if (!safeName.EndsWith(".nzb", StringComparison.OrdinalIgnoreCase))
                safeName += ".nzb";
            
            return File(nzbData, "application/x-nzb", safeName);
        }
        catch (Exception ex)
        {
            return StatusCode(500, $"Failed to download NZB: {ex.Message}");
        }
    }
}
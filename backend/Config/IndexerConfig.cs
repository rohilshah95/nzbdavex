namespace NzbWebDAV.Config;

public class IndexerConfig
{
    public List<ConnectionDetails> Indexers { get; set; } = [];

    public class ConnectionDetails
    {
        public required string Name { get; set; }
        public required string Url { get; set; }
        public required string ApiKey { get; set; }
        public bool Enabled { get; set; } = true;
        public string? UserAgent { get; set; }
        public int MaxRequestsPerMinute { get; set; } = 0;
        public bool EnableStrictMatching { get; set; } = false;
    }
}

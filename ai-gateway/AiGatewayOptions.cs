namespace OpenWorkmate.AI.Gateway;

public sealed class AiGatewayOptions
{
    public const string SectionName = "AiGateway";

    /// <summary>Shared secret for GET /api/policy/* (Bearer or X-Telemetry-Key)；与 AI 后台 user-config 一致。</summary>
    public string ApiKey { get; set; } = "";

    /// <summary>Secret for /api/admin/* JSON APIs (optional; if empty, only localhost may call admin).</summary>
    public string? AdminApiKey { get; set; }

    public string DataRoot { get; set; } = "data";

    public int RetentionDays { get; set; } = 30;

    public int RetentionSweepHours { get; set; } = 24;

    public int PolicyCacheSeconds { get; set; } = 30;

    public int MaxEventPayloadChars { get; set; } = 50_000;

    /// <summary>单 shard jsonl 超过此字节数时滚动（默认 32MB）。</summary>
    public int SessionJsonlShardMaxBytes { get; set; } = 32 * 1024 * 1024;

    /// <summary>大于此字节的 body 写入 blobs/，否则内联。</summary>
    public int BlobInlineMaxBytes { get; set; } = 8192;
}

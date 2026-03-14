namespace OfficeCopilot.Server.Services.Memory;

/// <summary>单条记忆记录，用于列表展示与 CRUD。</summary>
public sealed class MemoryRecord
{
    public string Id { get; set; } = "";
    public string Text { get; set; } = "";
    public string? SessionId { get; set; }
    public DateTime CreatedAt { get; set; }
    public IReadOnlyDictionary<string, string>? Metadata { get; set; }
}

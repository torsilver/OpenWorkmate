namespace OfficeCopilot.Server.Services.Memory;

/// <summary>记忆作用域常量：共享记忆使用固定 session_id，供跨端检索。</summary>
public static class MemoryScopes
{
    /// <summary>写入共享区时使用的 session_id；检索时用此值可只查共享记忆。</summary>
    public const string SharedSessionId = "__shared__";
}

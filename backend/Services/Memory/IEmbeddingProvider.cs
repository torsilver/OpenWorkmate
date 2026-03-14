namespace OfficeCopilot.Server.Services.Memory;

/// <summary>当前可用的嵌入服务；由 ChatService 在 RebuildKernelAsync 后设置，未配置时为 null。</summary>
public interface IEmbeddingProvider
{
    /// <summary>是否已配置嵌入模型（本地或远程）。</summary>
    bool IsConfigured { get; }
    /// <summary>为文本生成向量；未配置时返回 null。</summary>
    Task<float[]?> GenerateEmbeddingAsync(string text, CancellationToken ct = default);
}

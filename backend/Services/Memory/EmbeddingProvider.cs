using Microsoft.SemanticKernel.Embeddings;

namespace OfficeCopilot.Server.Services.Memory;

/// <summary>持有当前 Kernel 的嵌入服务引用，由 ChatService 在 RebuildKernelAsync 后更新。</summary>
#pragma warning disable CS0618 // ITextEmbeddingGenerationService 在 SK 1.72 中标记为过时，仍可用；后续可迁移至 IEmbeddingGenerator
public sealed class EmbeddingProvider : IEmbeddingProvider
{
    private volatile ITextEmbeddingGenerationService? _service;

    public bool IsConfigured => _service != null;

    public void SetService(ITextEmbeddingGenerationService? service)
    {
        _service = service;
    }

    public async Task<float[]?> GenerateEmbeddingAsync(string text, CancellationToken ct = default)
    {
        var svc = _service;
        if (svc == null || string.IsNullOrWhiteSpace(text)) return null;
        var data = new List<string> { text };
        var result = await svc.GenerateEmbeddingsAsync(data, cancellationToken: ct).ConfigureAwait(false);
        if (result == null || result.Count == 0) return null;
        var src = result[0];
        var arr = new float[src.Length];
        src.CopyTo(arr);
        return arr;
    }
}
#pragma warning restore CS0618

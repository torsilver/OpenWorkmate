using Microsoft.Extensions.AI;

namespace OpenWorkmate.Server.Services.Memory;

/// <summary>持有当前嵌入服务引用（MEAI <see cref="IEmbeddingGenerator{String, Embedding}"/>），由 ChatService 在 RebuildRuntimeAsync 后更新。</summary>
public sealed class EmbeddingProvider : IEmbeddingProvider
{
    private volatile IEmbeddingGenerator<string, Embedding<float>>? _generator;

    public bool IsConfigured => _generator != null;

    public void SetGenerator(IEmbeddingGenerator<string, Embedding<float>>? generator)
    {
        _generator = generator;
    }

    public async Task<float[]?> GenerateEmbeddingAsync(string text, CancellationToken ct = default)
    {
        var gen = _generator;
        if (gen == null || string.IsNullOrWhiteSpace(text)) return null;
        var result = await gen.GenerateAsync([text], cancellationToken: ct).ConfigureAwait(false);
        if (result is not { Count: > 0 }) return null;
        return result[0].Vector.ToArray();
    }
}

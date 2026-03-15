using Microsoft.SemanticKernel;
using OfficeCopilot.Server.Services.Memory;

namespace OfficeCopilot.Server.Services;

/// <summary>
/// 工具向量索引：按 clientType 分 collection 存储工具描述，支持按用户输入检索；仅对工具（函数）建索引，子类不向量化。
/// </summary>
public sealed class ToolIndexService : IToolIndexService
{
    private const string CollectionPrefix = "tools:";

    private static readonly string[] ClientTypes = { "chrome", "office-word", "office-excel", "office-powerpoint", "wps" };

    private readonly IEmbeddingProvider _embedding;
    private readonly IVectorStore _store;
    private readonly ILogger<ToolIndexService> _logger;

    public ToolIndexService(IEmbeddingProvider embedding, IVectorStore store, ILogger<ToolIndexService> logger)
    {
        _embedding = embedding;
        _store = store;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task BuildIndexAsync(Kernel kernel, CancellationToken ct = default)
    {
        if (kernel == null || !_embedding.IsConfigured)
        {
            _logger.LogDebug("ToolIndex: Skip build (kernel null or embedding not configured).");
            return;
        }

        foreach (var clientType in ClientTypes)
        {
            var count = 0;
            foreach (var plugin in kernel.Plugins)
            {
                foreach (KernelFunction func in plugin)
                {
                    if (!ClientTypeToolFilter.IsAllowed(plugin.Name, func.Name, clientType))
                        continue;
                    var id = $"{plugin.Name}|{func.Name}";
                    var text = BuildToolText(plugin.Name, func.Name, func);
                    var vector = await _embedding.GenerateEmbeddingAsync(text, ct).ConfigureAwait(false);
                    if (vector == null || vector.Length == 0)
                        continue;
                    var collection = CollectionPrefix + clientType;
                    await _store.UpsertAsync(id, text, vector, null, null, collection, ct).ConfigureAwait(false);
                    count++;
                }
            }
            _logger.LogDebug("ToolIndex: Built collection {Collection} with {Count} tools.", CollectionPrefix + clientType, count);
        }
    }

    /// <inheritdoc />
    public async Task<(IReadOnlyList<(string PluginName, string FunctionName)> Results, bool GoodEnough)> SearchToolsAsync(
        string userQuery,
        string? clientType,
        int topK = 20,
        double minScore = 0.7,
        int minCount = 1,
        CancellationToken ct = default)
    {
        var effectiveClientType = string.IsNullOrWhiteSpace(clientType) ? "chrome" : clientType.Trim();
        var collection = CollectionPrefix + effectiveClientType;

        if (!_embedding.IsConfigured || string.IsNullOrWhiteSpace(userQuery))
        {
            _logger.LogDebug("ToolIndex: Skip search (embedding not configured or empty query).");
            return (Array.Empty<(string, string)>(), false);
        }

        var queryVector = await _embedding.GenerateEmbeddingAsync(userQuery, ct).ConfigureAwait(false);
        if (queryVector == null || queryVector.Length == 0)
            return (Array.Empty<(string, string)>(), false);

        var hits = await _store.SearchAsync(queryVector, Math.Clamp(topK, 1, 50), null, collection, ct).ConfigureAwait(false);
        var results = new List<(string PluginName, string FunctionName)>();
        foreach (var (id, _, score) in hits)
        {
            var pair = ParseToolId(id);
            if (pair.HasValue)
                results.Add(pair.Value);
        }

        var maxScore = hits.Count > 0 ? hits[0].Score : 0.0;
        var goodEnough = results.Count >= minCount && maxScore >= minScore;
        _logger.LogDebug("ToolIndex: Search collection={Collection} results={Count} maxScore={Score} goodEnough={GoodEnough}",
            collection, results.Count, maxScore, goodEnough);
        return (results, goodEnough);
    }

    private static string BuildToolText(string pluginName, string functionName, KernelFunction func)
    {
        var desc = GetFunctionDescription(func);
        var parts = new List<string> { pluginName, functionName };
        if (!string.IsNullOrWhiteSpace(desc))
            parts.Add(desc);
        return string.Join(" ", parts);
    }

    private static string? GetFunctionDescription(KernelFunction func)
    {
        try
        {
            var meta = func.Metadata;
            if (meta == null) return null;
            return meta.Description;
        }
        catch
        {
            return null;
        }
    }

    private static (string PluginName, string FunctionName)? ParseToolId(string id)
    {
        if (string.IsNullOrEmpty(id)) return null;
        var i = id.IndexOf('|');
        if (i <= 0 || i >= id.Length - 1) return null;
        return (id[..i], id[(i + 1)..]);
    }
}

using Microsoft.SemanticKernel;
using OfficeCopilot.Server.Services.Memory;

namespace OfficeCopilot.Server.Services;

/// <summary>
/// 工具向量索引：按 clientType 分 collection 存储工具描述，支持按用户输入检索；仅对工具（函数）建索引，子类不向量化。
/// </summary>
public sealed class ToolIndexService : IToolIndexService
{
    private const string CollectionPrefix = "tools:";
    private const string ToolSourceBuiltin = "builtin";
    private const string ToolSourceUser = "user";

    private static readonly string[] ClientTypes = { "chrome", "office-word", "office-excel", "office-powerpoint", "wps" };

    /// <summary>内置插件名（与 ChatService 中在加载 UserSkill/MCP 之前注册的插件一致）。</summary>
    private static readonly HashSet<string> BuiltinPluginNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CLI", "Excel", "Word", "Ppt", "Browser", "File", "System",
        "MCP_STT", "MCP_OCR", "CurrentDocument", "Tavily", "ClawhubSkill",
        "Memory", "Context", "Subagent", "CrossAgentTask", "Plan",
        "UserOptions", "AccurateData", "ScheduledTask"
    };

    private static bool IsUserPlugin(string pluginName)
    {
        if (string.IsNullOrEmpty(pluginName)) return false;
        if (pluginName.StartsWith("UserSkill_", StringComparison.OrdinalIgnoreCase)) return true;
        if (!pluginName.StartsWith("MCP_", StringComparison.OrdinalIgnoreCase)) return false;
        return !string.Equals(pluginName, "MCP_STT", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(pluginName, "MCP_OCR", StringComparison.OrdinalIgnoreCase);
    }

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
    public async Task BuildIndexAsync(Kernel kernel, ToolIndexBuildMode mode = ToolIndexBuildMode.UserOnly, CancellationToken ct = default)
    {
        if (kernel == null || !_embedding.IsConfigured)
        {
            _logger.LogDebug("ToolIndex: Skip build (kernel null or embedding not configured).");
            return;
        }
        if (!_store.IsPersistent)
        {
            _logger.LogDebug("ToolIndex: Skip build (store is in-memory, use two-round tool selection).");
            return;
        }

        if (mode == ToolIndexBuildMode.BuiltinOnly)
        {
            var deleted = await _store.DeleteByToolSourceAsync(CollectionPrefix, ToolSourceBuiltin, ct).ConfigureAwait(false);
            if (deleted > 0) _logger.LogDebug("ToolIndex: Deleted {Count} existing builtin tool vectors.", deleted);
        }
        else if (mode == ToolIndexBuildMode.UserOnly)
        {
            var deleted = await _store.DeleteByToolSourceAsync(CollectionPrefix, ToolSourceUser, ct).ConfigureAwait(false);
            if (deleted > 0) _logger.LogDebug("ToolIndex: Deleted {Count} existing user tool vectors.", deleted);
        }

        foreach (var clientType in ClientTypes)
        {
            var collection = CollectionPrefix + clientType;
            var count = 0;
            foreach (var plugin in kernel.Plugins)
            {
                if (mode == ToolIndexBuildMode.BuiltinOnly && !BuiltinPluginNames.Contains(plugin.Name))
                    continue;
                if (mode == ToolIndexBuildMode.UserOnly && !IsUserPlugin(plugin.Name))
                    continue;

                foreach (KernelFunction func in plugin)
                {
                    if (!ClientTypeToolFilter.IsAllowed(plugin.Name, func.Name, clientType))
                        continue;
                    var id = $"{collection}|{plugin.Name}|{func.Name}";
                    var text = BuildToolText(plugin.Name, func.Name, func);
                    var existing = await _store.GetAsync(id, ct).ConfigureAwait(false);
                    if (existing.HasValue && existing.Value.Text == text)
                        continue;
                    var vector = await _embedding.GenerateEmbeddingAsync(text, ct).ConfigureAwait(false);
                    if (vector == null || vector.Length == 0)
                        continue;
                    var toolSource = mode == ToolIndexBuildMode.BuiltinOnly ? ToolSourceBuiltin
                        : mode == ToolIndexBuildMode.UserOnly ? ToolSourceUser : null;
                    await _store.UpsertAsync(id, text, vector, null, null, collection, toolSource, ct).ConfigureAwait(false);
                    count++;
                }
            }
            _logger.LogDebug("ToolIndex: Built collection {Collection} with {Count} tools (mode={Mode}).", collection, count, mode);
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
        var queryPreview = string.IsNullOrEmpty(userQuery) ? "" : (userQuery.Length <= 80 ? userQuery : userQuery[..80] + "...");
        _logger.LogInformation("ToolIndex: Search entry clientType={ClientType} collection={Collection} queryLen={QueryLen} topK={TopK} minScore={MinScore} minCount={MinCount}",
            effectiveClientType, collection, userQuery?.Length ?? 0, topK, minScore, minCount);

        if (!_embedding.IsConfigured || string.IsNullOrWhiteSpace(userQuery))
        {
            _logger.LogDebug("ToolIndex: Skip search (embedding not configured or empty query).");
            var reason = !_embedding.IsConfigured ? "embedding not configured" : "query empty";
            _logger.LogInformation("ToolIndex: Skip search, reason={Reason}.", reason);
            return (Array.Empty<(string, string)>(), false);
        }

        var queryVector = await _embedding.GenerateEmbeddingAsync(userQuery, ct).ConfigureAwait(false);
        if (queryVector == null || queryVector.Length == 0)
            return (Array.Empty<(string, string)>(), false);

        var hits = await _store.SearchAsync(queryVector, Math.Clamp(topK, 1, 50), null, collection, ct).ConfigureAwait(false);
        var results = new List<(string PluginName, string FunctionName)>();
        var collectionPrefix = collection + "|";
        foreach (var (id, _, score) in hits)
        {
            var toolId = id.StartsWith(collectionPrefix, StringComparison.Ordinal) ? id.Substring(collectionPrefix.Length) : id;
            var pair = ParseToolId(toolId);
            if (pair.HasValue)
                results.Add(pair.Value);
        }

        var maxScore = hits.Count > 0 ? hits[0].Score : 0.0;
        var goodEnough = results.Count >= minCount && maxScore >= minScore;
        _logger.LogInformation("ToolIndex: Search result collection={Collection} resultsCount={Count} maxScore={Score:F4} goodEnough={GoodEnough} queryPreview={QueryPreview}",
            collection, results.Count, maxScore, goodEnough, queryPreview);
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

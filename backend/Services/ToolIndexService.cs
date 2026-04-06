using Microsoft.Extensions.AI;
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
        "Memory", "Context", "Subagent", "CrossAgentTask", "Plan", "SkillAuthor",
        "UserOptions", "AccurateData", "MeetingTranscript", "ScheduledTask"
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
    public async Task BuildIndexAsync(ToolRegistry toolRegistry, ToolIndexBuildMode mode = ToolIndexBuildMode.UserOnly, CancellationToken ct = default)
    {
        if (toolRegistry == null || !_embedding.IsConfigured)
        {
            _logger.LogDebug("ToolIndex: Skip build (registry null or embedding not configured).");
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
            foreach (var pluginName in toolRegistry.GetPluginNames())
            {
                if (mode == ToolIndexBuildMode.BuiltinOnly && !BuiltinPluginNames.Contains(pluginName))
                    continue;
                if (mode == ToolIndexBuildMode.UserOnly && !IsUserPlugin(pluginName))
                    continue;

                foreach (var (pName, fName, tool) in toolRegistry.GetToolsByPlugin(pluginName))
                {
                    if (!ClientTypeToolFilter.IsAllowed(pName, fName, clientType))
                        continue;
                    var id = $"{collection}|{pName}|{fName}";
                    var text = BuildToolText(pName, fName, tool);
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
    public async Task SyncUserToolIndexAsync(ToolRegistry toolRegistry, CancellationToken ct = default)
    {
        if (toolRegistry == null || !_embedding.IsConfigured)
        {
            _logger.LogDebug("ToolIndex: Skip SyncUserToolIndex (registry null or embedding not configured).");
            return;
        }
        if (!_store.IsPersistent)
        {
            _logger.LogDebug("ToolIndex: Skip SyncUserToolIndex (store is in-memory).");
            return;
        }

        foreach (var clientType in ClientTypes)
        {
            var collection = CollectionPrefix + clientType;
            var expectedIds = new HashSet<string>(StringComparer.Ordinal);
            var upserted = 0;
            foreach (var pluginName in toolRegistry.GetPluginNames())
            {
                if (!IsUserPlugin(pluginName))
                    continue;
                foreach (var (pName, fName, tool) in toolRegistry.GetToolsByPlugin(pluginName))
                {
                    if (!ClientTypeToolFilter.IsAllowed(pName, fName, clientType))
                        continue;
                    var id = $"{collection}|{pName}|{fName}";
                    expectedIds.Add(id);
                    var text = BuildToolText(pName, fName, tool);
                    var existing = await _store.GetAsync(id, ct).ConfigureAwait(false);
                    if (existing.HasValue && existing.Value.Text == text)
                        continue;
                    var vector = await _embedding.GenerateEmbeddingAsync(text, ct).ConfigureAwait(false);
                    if (vector == null || vector.Length == 0)
                        continue;
                    await _store.UpsertAsync(id, text, vector, null, null, collection, ToolSourceUser, ct).ConfigureAwait(false);
                    upserted++;
                }
            }

            var storedIds = await _store.ListIdsByCollectionAndToolSourceAsync(collection, ToolSourceUser, ct).ConfigureAwait(false);
            var removed = 0;
            foreach (var sid in storedIds)
            {
                if (expectedIds.Contains(sid))
                    continue;
                if (await _store.DeleteAsync(sid, ct).ConfigureAwait(false))
                    removed++;
            }

            _logger.LogInformation(
                "ToolIndex: SyncUserToolIndex collection={Collection} upserted={Upserted} orphansRemoved={Removed}.",
                collection, upserted, removed);
        }
    }

    /// <inheritdoc />
    public async Task<ToolSearchResult> SearchToolsAsync(
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
            return new ToolSearchResult(Array.Empty<(string, string)>(), false, Array.Empty<(string, string, double)>());
        }

        var queryVector = await _embedding.GenerateEmbeddingAsync(userQuery, ct).ConfigureAwait(false);
        if (queryVector == null || queryVector.Length == 0)
            return new ToolSearchResult(Array.Empty<(string, string)>(), false, Array.Empty<(string, string, double)>());

        var hits = await _store.SearchAsync(queryVector, Math.Clamp(topK, 1, 50), null, collection, ct).ConfigureAwait(false);
        var bestScoreByPair = new Dictionary<(string Plugin, string Function), double>(new PluginFunctionScoreComparer());
        var collectionPrefix = collection + "|";
        foreach (var (id, _, score) in hits)
        {
            var toolId = id.StartsWith(collectionPrefix, StringComparison.Ordinal) ? id.Substring(collectionPrefix.Length) : id;
            var pair = ParseToolId(toolId);
            if (!pair.HasValue) continue;
            var key = (pair.Value.PluginName, pair.Value.FunctionName);
            if (!bestScoreByPair.TryGetValue(key, out var prev) || score > prev)
                bestScoreByPair[key] = score;
        }

        var scoredHits = bestScoreByPair
            .OrderByDescending(kv => kv.Value)
            .Select(kv => (PluginName: kv.Key.Plugin, FunctionName: kv.Key.Function, Score: kv.Value))
            .ToList();
        var results = scoredHits.Select(h => (h.PluginName, h.FunctionName)).ToList();

        var maxScore = scoredHits.Count > 0 ? scoredHits[0].Score : 0.0;
        var goodEnough = scoredHits.Count >= minCount && maxScore >= minScore;
        _logger.LogInformation("ToolIndex: Search result collection={Collection} resultsCount={Count} maxScore={Score:F4} goodEnough={GoodEnough} queryPreview={QueryPreview}",
            collection, scoredHits.Count, maxScore, goodEnough, queryPreview);
        return new ToolSearchResult(results, goodEnough, scoredHits);
    }

    private sealed class PluginFunctionScoreComparer : IEqualityComparer<(string Plugin, string Function)>
    {
        public bool Equals((string Plugin, string Function) x, (string Plugin, string Function) y) =>
            string.Equals(x.Plugin, y.Plugin, StringComparison.OrdinalIgnoreCase)
            && string.Equals(x.Function, y.Function, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode((string Plugin, string Function) obj) =>
            StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Plugin) ^ StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Function);
    }

    private static string BuildToolText(string pluginName, string functionName, AITool tool)
    {
        var desc = (tool as AIFunction)?.Description;
        var parts = new List<string> { pluginName, functionName };
        if (!string.IsNullOrWhiteSpace(desc))
            parts.Add(desc);
        return string.Join(" ", parts);
    }

    private static (string PluginName, string FunctionName)? ParseToolId(string id)
    {
        if (string.IsNullOrEmpty(id)) return null;
        var i = id.IndexOf('|');
        if (i <= 0 || i >= id.Length - 1) return null;
        return (id[..i], id[(i + 1)..]);
    }
}

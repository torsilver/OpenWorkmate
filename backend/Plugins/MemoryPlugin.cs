using System.ComponentModel;
using System.Text.Json;
using OpenWorkmate.Server;
using OpenWorkmate.Server.Services;
using OpenWorkmate.Server.Services.Memory;
using OpenWorkmate.Server.Services.ToolInvocation;

namespace OpenWorkmate.Server.Plugins;

/// <summary>阶段 3：长期记忆插件，供模型在对话中主动「记住」与「检索」记忆。</summary>
[OpenWorkmatePluginId("Memory")]
public sealed class MemoryPlugin
{
    private readonly IMemoryStoreService _memory;
    private readonly SessionManager _sessionManager;
    private readonly ILogger<MemoryPlugin>? _logger;

    public MemoryPlugin(IMemoryStoreService memory, SessionManager sessionManager, ILogger<MemoryPlugin>? logger = null)
    {
        _memory = memory;
        _sessionManager = sessionManager;
        _logger = logger;
    }

    [ToolFunction("save_memory")]
    [Description("将用户或对话中的一条重要信息保存为长期记忆，便于后续对话检索。例如：用户偏好、关键事实、待办等。saveToShared 为 true 时写入共享记忆，其他端（如 Word/Chrome）也可检索到。")]
    public async Task<string> SaveMemoryAsync(
        [Description("要记住的文本内容")] string text,
        [Description("可选标签，逗号分隔，便于分类")] string tags = "",
        [Description("是否写入共享记忆（跨端可见）；仅对用户明确要求跨端记住的内容设为 true。JSON 布尔或字符串均可。")] JsonElement? saveToShared = null)
    {
        if (!ToolScalarArgumentParser.TryReadBoolWithDefault(saveToShared, false, out var saveToSharedValue))
            return "[无效] saveToShared 参数无效：请使用 true/false 或字符串 \"true\"/\"false\"。";
        if (!_memory.IsAvailable)
            return "[记忆未启用] 未配置 Embedding 模型，无法保存记忆。请在设置中配置 Embedding 模型（本地或远程）。";
        if (string.IsNullOrWhiteSpace(text))
            return "[无效] 记忆内容不能为空。";
        var sessionId = SessionContext.GetSessionId();
        var agentName = !string.IsNullOrEmpty(sessionId) ? _sessionManager.GetDisplayName(sessionId) : null;
        var metadata = string.IsNullOrWhiteSpace(tags) ? null : new Dictionary<string, string> { ["tags"] = tags.Trim() };
        try
        {
            var id = await _memory.SaveAsync(null, text.Trim(), sessionId, metadata, saveToSharedValue, agentName).ConfigureAwait(false);
            _logger?.LogInformation("save_memory: id={Id} sessionId={SessionId} shared={Shared}", id, sessionId, saveToSharedValue);
            return saveToSharedValue ? $"[已记住] 已保存为共享记忆（id={id}），其他端也可检索。" : $"[已记住] 已保存为长期记忆（id={id}）。";
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "save_memory failed");
            var msg = ex.Message ?? "";
            if (msg.IndexOf("401", StringComparison.OrdinalIgnoreCase) >= 0 || msg.IndexOf("invalid_api_key", StringComparison.OrdinalIgnoreCase) >= 0 || msg.IndexOf("API key", StringComparison.OrdinalIgnoreCase) >= 0)
                return "[保存失败] Embedding 接口认证失败（请检查设置中的 Embedding API 密钥是否正确）。";
            return $"[保存失败] {msg}";
        }
    }

    [ToolFunction("search_memory")]
    [Description("根据查询从长期记忆中检索相关条目（本会话 + 共享记忆），返回与当前问题最相关的记忆列表；结果会标明来自本会话或共享。")]
    public async Task<string> SearchMemoryAsync(
        [Description("检索关键词或问题")] string query,
        [Description("返回条数，默认 5。JSON 数字或字符串均可。")] JsonElement? topK = null)
    {
        if (!ToolScalarArgumentParser.TryReadInt32WithDefault(topK, 5, out var topKValue))
            return "[无效] topK 无效：须为整数。";
        if (!_memory.IsAvailable)
            return "[记忆未启用] 未配置 Embedding 模型，无法检索记忆。";
        if (string.IsNullOrWhiteSpace(query))
            return "[无效] 查询不能为空。";
        var sessionId = SessionContext.GetSessionId();
        try
        {
            var sessionResults = await _memory.SearchAsync(query.Trim(), Math.Clamp(topKValue, 1, 20), sessionId).ConfigureAwait(false);
            var sharedResults = await _memory.SearchSharedAsync(query.Trim(), Math.Clamp(3, 1, 10)).ConfigureAwait(false);
            if (sessionResults.Count == 0 && sharedResults.Count == 0)
                return "[无相关记忆] 未找到与当前查询相关的记忆。";
            var sb = new System.Text.StringBuilder();
            if (sessionResults.Count > 0)
            {
                sb.AppendLine("[本会话记忆]");
                foreach (var (id, text, score) in sessionResults)
                {
                    var scorePct = (score * 100).ToString("F0");
                    sb.AppendLine($"- [{id}] (相关度 {scorePct}%) {text}");
                }
            }
            if (sharedResults.Count > 0)
            {
                sb.AppendLine("[来自共享记忆]");
                foreach (var (id, text, score) in sharedResults)
                {
                    var scorePct = (score * 100).ToString("F0");
                    sb.AppendLine($"- [{id}] (相关度 {scorePct}%) {text}");
                }
            }
            return sb.ToString();
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "search_memory failed");
            return $"[检索失败] {ex.Message}";
        }
    }
}

using System.ComponentModel;
using Microsoft.SemanticKernel;
using OfficeCopilot.Server.Services;
using OfficeCopilot.Server.Services.Memory;

namespace OfficeCopilot.Server.Plugins;

/// <summary>阶段 3：长期记忆插件，供模型在对话中主动「记住」与「检索」记忆。</summary>
public sealed class MemoryPlugin
{
    private readonly IMemoryStoreService _memory;
    private readonly ILogger<MemoryPlugin>? _logger;

    public MemoryPlugin(IMemoryStoreService memory, ILogger<MemoryPlugin>? logger = null)
    {
        _memory = memory;
        _logger = logger;
    }

    [KernelFunction("save_memory")]
    [Description("将用户或对话中的一条重要信息保存为长期记忆，便于后续对话检索。例如：用户偏好、关键事实、待办等。")]
    public async Task<string> SaveMemoryAsync(
        [Description("要记住的文本内容")] string text,
        [Description("可选标签，逗号分隔，便于分类")] string tags = "")
    {
        if (!_memory.IsAvailable)
            return "[记忆未启用] 未配置 Embedding 模型，无法保存记忆。请在设置中配置 Embedding 模型（本地或远程）。";
        if (string.IsNullOrWhiteSpace(text))
            return "[无效] 记忆内容不能为空。";
        var sessionId = SessionContext.GetSessionId();
        var metadata = string.IsNullOrWhiteSpace(tags) ? null : new Dictionary<string, string> { ["tags"] = tags.Trim() };
        try
        {
            var id = await _memory.SaveAsync(null, text.Trim(), sessionId, metadata).ConfigureAwait(false);
            _logger?.LogInformation("save_memory: id={Id} sessionId={SessionId}", id, sessionId);
            return $"[已记住] 已保存为长期记忆（id={id}）。";
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "save_memory failed");
            return $"[保存失败] {ex.Message}";
        }
    }

    [KernelFunction("search_memory")]
    [Description("根据查询从长期记忆中检索相关条目，返回与当前问题最相关的记忆列表。")]
    public async Task<string> SearchMemoryAsync(
        [Description("检索关键词或问题")] string query,
        [Description("返回条数，默认 5")] int topK = 5)
    {
        if (!_memory.IsAvailable)
            return "[记忆未启用] 未配置 Embedding 模型，无法检索记忆。";
        if (string.IsNullOrWhiteSpace(query))
            return "[无效] 查询不能为空。";
        var sessionId = SessionContext.GetSessionId();
        try
        {
            var results = await _memory.SearchAsync(query.Trim(), Math.Clamp(topK, 1, 20), sessionId).ConfigureAwait(false);
            if (results.Count == 0)
                return "[无相关记忆] 未找到与当前查询相关的记忆。";
            var sb = new System.Text.StringBuilder();
            foreach (var (id, text, score) in results)
            {
                var scorePct = (score * 100).ToString("F0");
                sb.AppendLine($"- [{id}] (相关度 {scorePct}%) {text}");
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

using System.Collections.Frozen;
using Microsoft.SemanticKernel.ChatCompletion;

namespace OfficeCopilot.Server.Services;

/// <summary>
/// 按需工具选择：Keyword 模式用关键词匹配从可用插件中选出本轮可能用到的插件名。
/// </summary>
public sealed class ToolSelectionService : IToolSelector
{
    private readonly ConfigService _configService;
    private readonly ILogger<ToolSelectionService> _logger;

    /// <summary>内置插件默认关键词表（插件名 -> 触发关键词，不区分大小写匹配）。</summary>
    private static readonly FrozenDictionary<string, string[]> DefaultPluginKeywords = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
    {
        ["CLI"] = new[] { "命令", "cmd", "运行", "command", "执行", "终端", "shell" },
        ["Excel"] = new[] { "表格", "excel", "单元格", "工作表", "xlsx", "电子表格", "表格" },
        ["Word"] = new[] { "word", "文档", "docx", "段落", "格式", "doc" },
        ["Browser"] = new[] { "浏览器", "网页", "截图", "高亮", "笔记", "browser", "页面", "标签页" },
        ["File"] = new[] { "文件", "保存", "下载", "file", "写入" },
        ["Tavily"] = new[] { "搜索", "查", "网上", "search", "新闻", "tavily" },
        ["ClawhubSkill"] = new[] { "技能", "clawhub", "脚本" },
    }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    public ToolSelectionService(ConfigService configService, ILogger<ToolSelectionService> logger)
    {
        _configService = configService;
        _logger = logger;
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<string>> SelectPluginNamesAsync(
        string userMessage,
        ChatHistory? recentHistory,
        IReadOnlyList<string> availablePluginNames,
        CancellationToken ct = default)
    {
        if (availablePluginNames == null || availablePluginNames.Count == 0)
            return Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());

        var ai = _configService.Current.AI;

        var mode = (ai.ToolSelectionMode ?? "Keyword").Trim();
        if (!string.Equals(mode, "Keyword", StringComparison.OrdinalIgnoreCase))
        {
            // LLM 模式暂未实现，返回空表示“不限制”、使用全量工具
            _logger.LogDebug("ToolSelection Mode={Mode} not implemented, using all tools.", mode);
            return Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());
        }

        var textToMatch = userMessage ?? "";
        if (recentHistory != null && recentHistory.Count > 0)
        {
            var lastContent = recentHistory[^1].Content ?? "";
            if (lastContent.Length > 0 && lastContent.Length < 2000)
                textToMatch = textToMatch + "\n" + lastContent;
        }

        var selected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // AlwaysIncludePlugins 始终加入
        foreach (var name in ai.AlwaysIncludePlugins ?? new List<string>())
        {
            if (!string.IsNullOrWhiteSpace(name))
                selected.Add(name.Trim());
        }

        // 关键词匹配：只考虑 availablePluginNames 中存在的插件
        foreach (var pluginName in availablePluginNames)
        {
            if (string.IsNullOrWhiteSpace(pluginName)) continue;
            if (selected.Contains(pluginName)) continue;

            if (DefaultPluginKeywords.TryGetValue(pluginName, out var keywords))
            {
                foreach (var kw in keywords)
                {
                    if (string.IsNullOrEmpty(kw)) continue;
                    if (textToMatch.Contains(kw, StringComparison.OrdinalIgnoreCase))
                    {
                        selected.Add(pluginName);
                        break;
                    }
                }
            }
            // UserSkill_*、MCP_* 等：用通用关键词匹配
            else if (pluginName.StartsWith("UserSkill_", StringComparison.OrdinalIgnoreCase) &&
                     (textToMatch.Contains("技能", StringComparison.OrdinalIgnoreCase) ||
                      textToMatch.Contains("excel", StringComparison.OrdinalIgnoreCase) ||
                      textToMatch.Contains("word", StringComparison.OrdinalIgnoreCase) ||
                      textToMatch.Contains("截图", StringComparison.OrdinalIgnoreCase)))
            {
                selected.Add(pluginName);
            }
        }

        var result = selected.ToList();
        if (result.Count > 0)
            _logger.LogDebug("ToolSelection selected {Count} plugins: {Plugins}", result.Count, string.Join(", ", result));

        return Task.FromResult<IReadOnlyList<string>>(result);
    }
}

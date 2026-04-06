using System.ComponentModel;
using System.Text;
using Microsoft.Extensions.AI;
using OfficeCopilot.Server;
using OfficeCopilot.Server.Services;
using OfficeCopilot.Server.Services.DashScope;

namespace OfficeCopilot.Server.Plugins;

/// <summary>根据对话意图生成并保存用户技能（SKILL.md），与设置页技能列表同源。</summary>
public sealed class SkillAuthorPlugin
{
    private readonly SkillService _skillService;
    private readonly IChatRuntimeAccessor _runtime;
    private readonly ILogger<SkillAuthorPlugin> _logger;

    public SkillAuthorPlugin(SkillService skillService, IChatRuntimeAccessor runtimeAccessor, ILogger<SkillAuthorPlugin> logger)
    {
        _skillService = skillService;
        _runtime = runtimeAccessor;
        _logger = logger;
    }

    [ToolFunction("generate_user_skill")]
    [Description("根据用户目标与可选对话摘要，调用模型生成一份完整的 SKILL.md（含 YAML frontmatter），保存为可复用用户技能。用户明确要求「把刚才聊的做成技能」等时使用。生成内容中应写明推荐使用的内置插件名（如 Excel、Word、Memory）以便后续工具选择命中。")]
    public async Task<string> GenerateUserSkillAsync(
        [Description("用户希望技能解决什么问题（必填）")] string goal,
        [Description("可选：当前对话摘要或关键要点，便于生成贴合语境的技能说明")] string? context = null,
        [Description("若已存在同名技能，为 true 则覆盖；默认 false")] bool overwrite = false,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(goal))
            return "[技能生成失败] goal 不能为空。";

        var client = _runtime.GetChatClient();
        if (client == null)
            return "[技能生成失败] 未找到对话服务。";

        var systemPrompt = BuildSkillGenerationSystemPrompt();
        var userContent = string.IsNullOrWhiteSpace(context)
            ? $"目标：{goal.Trim()}"
            : $"目标：{goal.Trim()}\n\n上下文（对话摘要或要点）：{context.Trim()}";

        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, systemPrompt),
            new(ChatRole.User, userContent)
        };

        var options = new ChatOptions { MaxOutputTokens = 4000, Temperature = 0.3f };
        var sb = new StringBuilder();
        try
        {
            using (DashScopeCallKindContext.EnterBackground())
            {
                await foreach (var update in client.GetStreamingResponseAsync(messages, options, ct).ConfigureAwait(false))
                {
                    if (update.Text is { Length: > 0 } text)
                        sb.Append(text);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "generate_user_skill: LLM call failed");
            return $"[技能生成失败] {ex.Message}";
        }

        var content = NormalizeGeneratedSkillMarkdown(sb.ToString());
        if (string.IsNullOrEmpty(content))
            return "[技能生成失败] 模型未返回内容。";

        return SaveParsedSkillMarkdown(content, overwrite);
    }

    [ToolFunction("save_user_skill_markdown")]
    [Description("将已写好的完整 SKILL.md 文本保存为用户技能（与设置页同源）。当模型已在回复中写出全文时使用，避免再次调用生成。")]
    public Task<string> SaveUserSkillMarkdownAsync(
        [Description("完整 SKILL.md：含 --- 包裹的 YAML（name、description、可选 title/enabled）与正文")] string skillMarkdown,
        [Description("若已存在同名技能，为 true 则覆盖；默认 false")] bool overwrite = false,
        CancellationToken ct = default)
    {
        _ = ct;
        if (string.IsNullOrWhiteSpace(skillMarkdown))
            return Task.FromResult("[技能保存失败] skillMarkdown 不能为空。");
        var normalized = NormalizeGeneratedSkillMarkdown(skillMarkdown);
        return Task.FromResult(SaveParsedSkillMarkdown(normalized, overwrite));
    }

    private string SaveParsedSkillMarkdown(string content, bool overwrite)
    {
        if (!_skillService.TryParseSkillMarkdown(content, out var skill, out var parseError))
            return parseError ?? "[技能保存失败] 解析失败。";

        if (!overwrite && _skillService.SkillExists(skill.Id))
            return $"[技能保存失败] 已存在技能 id={skill.Id}。若需覆盖请设置 overwrite=true，或在设置页先删除该技能。";

        try
        {
            _skillService.SaveSkill(skill);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "save skill failed id={Id}", skill.Id);
            return $"[技能保存失败] {ex.Message}";
        }

        _logger.LogInformation("User skill saved: id={Id} name={Name}", skill.Id, skill.Name);
        return $"[技能已保存] id={skill.Id}，标题：{skill.Name}。可在设置页「技能」中查看或编辑；新技能将在内核重建后生效（通常立即）。";
    }

    private static string NormalizeGeneratedSkillMarkdown(string raw)
    {
        var t = raw.Trim();
        if (t.StartsWith("```", StringComparison.Ordinal))
        {
            var firstNl = t.IndexOf('\n');
            if (firstNl >= 0)
                t = t[(firstNl + 1)..].TrimStart();
            var fence = t.LastIndexOf("```", StringComparison.Ordinal);
            if (fence >= 0)
                t = t[..fence].Trim();
        }
        return t.Trim();
    }

    private static string BuildSkillGenerationSystemPrompt()
    {
        return """
你是 Office Copilot 用户技能（Agent Skill）撰写助手。根据用户给出的「目标」与可选「上下文」，输出一份 **完整** 的 SKILL.md 文件内容。

## 输出格式（必须严格遵守）
1. 第一行起为 YAML frontmatter：以单独一行的 --- 开始，接着若干行键值，再以单独一行的 --- 结束。
2. frontmatter 必须包含：
   - name: 技能唯一标识，仅使用小写字母、数字、下划线或连字符（将作为文件夹名），例如 my_weekly_report
   - description: 一句话说明何时应使用该技能（会用于工具与技能检索，务必具体）
   - 可选 title: 展示用中文标题（不写则使用 name）
   - 可选 enabled: true 或 false（默认 true）
3. 第二个 --- 之后为 Markdown 正文：写给模型看的操作说明、步骤、注意事项；可含二级标题等。
4. **只输出 SKILL.md 原文**，不要输出「好的」「以下是」等前缀，不要用 markdown 代码块包裹全文。

## 内容质量要求
- 正文中应 **显式列出推荐使用的内置插件名**（须与系统中实际名称一致），例如 Excel、Word、Ppt、Browser、File、Memory、Plan、MCP_OCR、CurrentDocument、Tavily、ClawhubSkill 等；需要时用 `插件名-函数名` 或说明可插入 `[TOOL:插件名]` 以便后续对话中工具选择命中。**不要编造不存在的插件名。** 维护时插件名应与后端工具选择字典及 Program 注册的插件类名一致（见仓库 docs/architecture-dimensions.md「Harness 与工具契约」）。
- 若上下文不足以确定工具名，写清「由用户或模型按任务选择」即可，勿臆造。

## YAML 注意
- description 若含冒号、引号等特殊字符，请用双引号包裹并正确转义。
""";
    }
}

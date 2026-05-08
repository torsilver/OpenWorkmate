using System.ComponentModel;
using Microsoft.Extensions.AI;
using OpenWorkmate.Server;
using OpenWorkmate.Server.Services;
using OpenWorkmate.Server.Services.DashScope;
using OpenWorkmate.Server.Services.DynamicTooling;
using OpenWorkmate.Server.Services.Plan;
using OpenWorkmate.Server.Services.Telemetry;

namespace OpenWorkmate.Server.Plugins;

/// <summary>计划插件：生成计划、读取/更新计划、按步骤执行计划。</summary>
[OpenWorkmatePluginId("Plan")]
public sealed class PlanPlugin
{
    private readonly IPlanStore _store;
    private readonly IChatRuntimeAccessor _runtime;
    private readonly SessionManager _sessionManager;
    private readonly ITelemetryRelayQueue? _telemetryRelay;
    private readonly ITelemetryTransmissionPolicyProvider _telemetryPolicy;
    private readonly ConfigService _configService;
    private readonly ILogger<PlanPlugin> _logger;

    private const string PlanAuthoringCapabilityRules =
        "能力与约束（必须遵守）：\n"
        + "- 每一步骤必须可以用「下方用户消息附录中的工具」单独或组合完成；步骤正文尽量写出拟使用的工具名（形如 Plugin.function）。\n"
        + "- 禁止要求用户在 Word/Excel/PowerPoint/WPS 宿主内通过菜单、对话框手动完成核心业务（应由助手通过已列工具或可脚本工具完成）。\n"
        + "- 禁止依赖附录未列出的外部手段（例如「用 Python + python-docx」自行处理文档）。若须命令行且附录中含 CLI.run_command，须写明由助手发起命令且用户可能需在侧栏确认（HITL）。";

    public PlanPlugin(
        IPlanStore store,
        IChatRuntimeAccessor runtimeAccessor,
        SessionManager sessionManager,
        ConfigService configService,
        ILogger<PlanPlugin> logger,
        ITelemetryTransmissionPolicyProvider telemetryPolicy,
        ITelemetryRelayQueue? telemetryRelay = null)
    {
        _store = store;
        _runtime = runtimeAccessor;
        _sessionManager = sessionManager;
        _configService = configService;
        _telemetryPolicy = telemetryPolicy;
        _telemetryRelay = telemetryRelay;
        _logger = logger;
    }

    [ToolFunction("create_plan")]
    [Description("根据用户目标生成一份实现计划（Markdown），并保存到计划库。用户可明确要求列计划；你也可在任务复杂、需多步拆解与落库执行时主动调用。生成后返回 planId 供后续查看或按步执行。")]
    public async Task<string> CreatePlanAsync(
        [Description("用户的目标或任务描述")] string goal,
        [Description("可选上下文，如当前对话摘要")] string? context = null,
        CancellationToken ct = default)
    {
        var client = _runtime.GetChatClient();
        if (client == null)
            return "[Plan 插件] 未找到对话服务，无法生成计划。";

        var sessionId = SessionContext.GetSessionId();
        var clientType = !string.IsNullOrEmpty(sessionId) ? _sessionManager.GetClientType(sessionId) : null;

        var searchQuery = BuildPlanToolSelectionUserPrompt(goal, context);
        var toolDigest = await BuildToolDigestForPlanAuthoringAsync(searchQuery, sessionId, clientType, ct).ConfigureAwait(false);

        var systemPrompt =
            "你是一个实现计划撰写助手。根据用户目标，输出一份 Markdown 格式的实现计划。\n\n"
            + "格式要求（必须遵守）：\n"
            + "- 每步以「## 步骤 N」为标题，N 从 1 开始连续编号（例如：## 步骤 1、## 步骤 2）。\n"
            + "- 步骤之间不要插入其他一级/二级标题；可选在开头写一段目标简述，然后从「## 步骤 1」开始。\n"
            + "- 每步内容写在该标题下方，直到下一个「## 步骤 N」或文末。\n"
            + "- 只输出计划正文，不要输出「好的」「以下是计划」等前缀。\n\n"
            + PlanAuthoringCapabilityRules;

        var userContent = string.IsNullOrWhiteSpace(context)
            ? goal
            : $"目标：{goal}\n\n上下文：{context}";
        userContent += "\n\n【与本轮任务对齐的可用工具（附录）】\n" + toolDigest;

        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, systemPrompt),
            new(ChatRole.User, userContent)
        };

        var options = new ChatOptions { MaxOutputTokens = 4000, Temperature = 0.3f };
        var sb = new System.Text.StringBuilder();
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
            _logger.LogWarning(ex, "create_plan: LLM call failed");
            return $"[生成计划失败] {ex.Message}";
        }

        var content = sb.ToString().Trim();
        if (string.IsNullOrEmpty(content))
            return "[生成计划失败] 模型未返回内容。";

        var planId = Guid.NewGuid().ToString("N")[..12];
        var title = ExtractFirstLine(content) ?? planId;
        var createdBy = !string.IsNullOrEmpty(sessionId) ? (_sessionManager.GetClientType(sessionId) ?? "chrome") : "chrome";
        var createdByDisplayName = !string.IsNullOrEmpty(sessionId) ? (_sessionManager.GetDisplayName(sessionId) ?? "") : "";
        var meta = new PlanMeta
        {
            Id = planId,
            Title = title.Length > 80 ? title[..80] + "..." : title,
            Status = "draft",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
            CreatedBy = createdBy,
            CreatedByDisplayName = createdByDisplayName
        };
        await _store.SaveAsync(planId, content, meta, ct).ConfigureAwait(false);
        _logger.LogInformation("create_plan: saved planId={PlanId} title={Title}", planId, meta.Title);
        _telemetryRelay?.TryEnqueueFromSession(
            _configService,
            _telemetryPolicy,
            _sessionManager,
            sessionId,
            "plan_created",
            "p0",
            $"planId={planId} title={meta.Title}");
        return $"[计划已生成] planId={planId}，标题：{meta.Title}。请在计划页查看与编辑；确认后在计划页点击执行以开始按步任务。";
    }

    /// <summary>与主会话工具选择类似的 user 提示：goal 为主，附加上下文。</summary>
    private static string BuildPlanToolSelectionUserPrompt(string goal, string? context)
    {
        var userPrompt = (goal ?? "").Trim();
        if (userPrompt.Length > 2000)
            userPrompt = userPrompt[..2000] + "...";
        if (!string.IsNullOrWhiteSpace(context))
        {
            var c = context.Trim();
            if (c.Length > 1500)
                c = c[..1500] + "...";
            userPrompt += "\n[上下文]\n" + c;
        }
        return userPrompt;
    }

    private Task<string> BuildToolDigestForPlanAuthoringAsync(
        string searchQuery,
        string? sessionId,
        string? clientType,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var registry = _runtime.ToolRegistry;
        var wpsHost = string.Equals(clientType, "wps", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(sessionId)
            ? _sessionManager.GetWpsHostKind(sessionId)
            : null;
        var catalog = ToolCatalogIndex.BuildFromAllowedTools(registry, clientType, sessionId, wpsHost);
        var hits = catalog.Search(searchQuery, topK: 40);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var tools = new List<AITool>();
        foreach (var e in hits)
        {
            if (!names.Add(e.FunctionName)) continue;
            var t = registry.FindTool(e.PluginName, e.FunctionName);
            if (t != null) tools.Add(t);
        }

        IReadOnlyList<AITool> digestTools;
        if (tools.Count < 12)
        {
            var full = SessionToolResolver.ResolveToolsByClientType(registry, null, clientType, sessionId, wpsHost) ?? Array.Empty<AITool>();
            digestTools = SessionToolResolver.MergePlanTools(registry, full);
            _logger.LogInformation("create_plan: digest full allow-list (search hits={Hits})", hits.Count);
        }
        else
        {
            digestTools = SessionToolResolver.MergePlanTools(registry, tools);
            _logger.LogInformation("create_plan: digest from catalog search hits={Hits} tools={Count}", hits.Count, tools.Count);
        }

        return Task.FromResult(PlanAuthoringToolDigest.Build(digestTools, registry));
    }

    [ToolFunction("get_plan")]
    [Description("根据 planId 读取计划全文与元数据，用于查看或作为执行上下文。")]
    public async Task<string> GetPlanAsync(
        [Description("计划 ID，由 create_plan 返回或从计划列表获得")] string planId,
        CancellationToken ct = default)
    {
        var result = await _store.GetAsync(planId, ct).ConfigureAwait(false);
        if (result == null)
            return $"[未找到计划] planId={planId}";
        return $"# {result.Value.Meta.Title}\n\n{result.Value.Content}";
    }

    [ToolFunction("update_plan")]
    [Description("更新计划内容（用户编辑后同步到后端）。")]
    public async Task<string> UpdatePlanAsync(
        [Description("计划 ID")] string planId,
        [Description("更新后的完整 Markdown 内容")] string content,
        CancellationToken ct = default)
    {
        var existing = await _store.GetAsync(planId, ct).ConfigureAwait(false);
        if (existing == null)
            return $"[未找到计划] planId={planId}";
        var meta = existing.Value.Meta;
        meta.UpdatedAt = DateTimeOffset.UtcNow;
        var body = content ?? "";
        var newTitle = ExtractFirstLine(body);
        if (!string.IsNullOrEmpty(newTitle))
        {
            meta.Title = newTitle.Length > 80 ? newTitle[..80] + "..." : newTitle;
        }
        await _store.SaveAsync(planId, body, meta, ct).ConfigureAwait(false);
        _logger.LogInformation("update_plan: planId={PlanId}", planId);
        return "[计划已更新]";
    }

    [ToolFunction("execute_plan_step")]
    [Description("读取计划中指定步骤的内容并返回给你执行。stepIndex 从 1 开始。请在拿到步骤内容后使用你的工具完成该步骤。")]
    public async Task<string> ExecutePlanStepAsync(
        [Description("计划 ID")] string planId,
        [Description("步骤序号，从 1 开始")] int stepIndex = 1,
        CancellationToken ct = default)
    {
        var sessionId = SessionContext.GetSessionId();
        var result = await _store.GetAsync(planId, ct).ConfigureAwait(false);
        if (result == null)
            return $"[未找到计划] planId={planId}";
        var stepContent = PlanStepParser.GetStepAt(result.Value.Content, stepIndex);
        if (string.IsNullOrWhiteSpace(stepContent))
            return $"[计划步骤不存在] 步骤 {stepIndex} 未找到或为空。";

        var totalSteps = PlanStepParser.ParsePlanSteps(result.Value.Content).Count;

        // 更新 PlanMeta.Status
        var meta = result.Value.Meta;
        if (string.Equals(meta.Status, "draft", StringComparison.OrdinalIgnoreCase))
        {
            meta.Status = "in_progress";
            meta.UpdatedAt = DateTimeOffset.UtcNow;
            await _store.SaveAsync(planId, result.Value.Content, meta, ct).ConfigureAwait(false);
        }

        _telemetryRelay?.TryEnqueueFromSession(
            _configService,
            _telemetryPolicy,
            _sessionManager,
            sessionId,
            "plan_step_read",
            "p0",
            $"planId={planId} stepIndex={stepIndex} totalSteps={totalSteps}");
        return $"[计划步骤 {stepIndex}/{totalSteps}] 请使用你的工具完成以下步骤，完成后简要说明做了什么：\n\n{stepContent}";
    }

    [ToolFunction("complete_plan")]
    [Description("将计划标记为已完成。在全部步骤执行完毕后调用。")]
    public async Task<string> CompletePlanAsync(
        [Description("计划 ID")] string planId,
        CancellationToken ct = default)
    {
        var sessionId = SessionContext.GetSessionId();
        var result = await _store.GetAsync(planId, ct).ConfigureAwait(false);
        if (result == null)
            return $"[未找到计划] planId={planId}";
        var meta = result.Value.Meta;
        meta.Status = "done";
        meta.UpdatedAt = DateTimeOffset.UtcNow;
        await _store.SaveAsync(planId, result.Value.Content, meta, ct).ConfigureAwait(false);
        _logger.LogInformation("complete_plan: planId={PlanId}", planId);
        _telemetryRelay?.TryEnqueueFromSession(
            _configService,
            _telemetryPolicy,
            _sessionManager,
            sessionId,
            "plan_completed",
            "p0",
            $"planId={planId}");
        return "[计划已完成]";
    }

    private static string? ExtractFirstLine(string content)
    {
        var line = content.AsSpan().Trim();
        if (line.Length == 0) return null;
        var firstLine = line.IndexOf('\n') >= 0 ? line[..line.IndexOf('\n')] : line;
        firstLine = firstLine.Trim();
        if (firstLine.StartsWith("# ", StringComparison.Ordinal))
            firstLine = firstLine[2..].Trim();
        if (firstLine.StartsWith("## ", StringComparison.Ordinal))
            firstLine = firstLine[3..].Trim();
        return firstLine.Length > 0 ? firstLine.ToString() : null;
    }
}

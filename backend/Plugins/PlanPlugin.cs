using System.ComponentModel;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel.Connectors.OpenAI;
using OfficeCopilot.Server;
using OfficeCopilot.Server.Services;
using OfficeCopilot.Server.Services.Plan;

namespace OfficeCopilot.Server.Plugins;

/// <summary>计划插件：生成计划、读取/更新计划、按步骤执行计划。</summary>
public sealed class PlanPlugin
{
    private readonly IPlanStore _store;
    private readonly IKernelAccessor _kernelAccessor;
    private readonly SessionManager _sessionManager;
    private readonly ConfigService _configService;
    private readonly ILogger<PlanPlugin> _logger;

    public PlanPlugin(IPlanStore store, IKernelAccessor kernelAccessor, SessionManager sessionManager, ConfigService configService, ILogger<PlanPlugin> logger)
    {
        _store = store;
        _kernelAccessor = kernelAccessor;
        _sessionManager = sessionManager;
        _configService = configService;
        _logger = logger;
    }

    [KernelFunction("create_plan")]
    [Description("根据用户目标生成一份实现计划（Markdown），并保存到计划库。用户可明确要求列计划；你也可在任务复杂、需多步拆解与落库执行时主动调用。生成后返回 planId 供后续查看或按步执行。")]
    public async Task<string> CreatePlanAsync(
        [Description("用户的目标或任务描述")] string goal,
        [Description("可选上下文，如当前对话摘要")] string? context = null,
        CancellationToken ct = default)
    {
        var kernel = _kernelAccessor.Kernel;
        if (kernel == null)
            return "[Plan 插件] 内核未就绪，无法生成计划。";

        var chat = kernel.Services.GetKeyedService<IChatCompletionService>(_kernelAccessor.ActiveModelId)
            ?? kernel.Services.GetService<IChatCompletionService>();
        if (chat == null)
            return "[Plan 插件] 未找到对话服务，无法生成计划。";

        var systemPrompt = "你是一个实现计划撰写助手。根据用户目标，输出一份 Markdown 格式的实现计划。\n\n格式要求（必须遵守）：\n- 每步以「## 步骤 N」为标题，N 从 1 开始连续编号（例如：## 步骤 1、## 步骤 2）。\n- 步骤之间不要插入其他一级/二级标题；可选在开头写一段目标简述，然后从「## 步骤 1」开始。\n- 每步内容写在该标题下方，直到下一个「## 步骤 N」或文末。\n- 只输出计划正文，不要输出「好的」「以下是计划」等前缀。";
        var userContent = string.IsNullOrWhiteSpace(context)
            ? goal
            : $"目标：{goal}\n\n上下文：{context}";

        var history = new ChatHistory(systemPrompt);
        history.AddUserMessage(userContent);

        var settings = new OpenAIPromptExecutionSettings { MaxTokens = 4000, Temperature = 0.3f };
        var sb = new System.Text.StringBuilder();
        try
        {
            await foreach (var msg in chat.GetStreamingChatMessageContentsAsync(history, settings, kernel, ct).ConfigureAwait(false))
            {
                if (msg.Content is { Length: > 0 } text)
                    sb.Append(text);
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
        var sessionId = SessionContext.GetSessionId();
        var createdBy = !string.IsNullOrEmpty(sessionId) ? (_sessionManager.GetClientType(sessionId) ?? "chrome") : "chrome";
        var createdByDisplayName = !string.IsNullOrEmpty(sessionId) ? (_sessionManager.GetDisplayName(sessionId) ?? "") : "";
        var steps = PlanStepParser.ParsePlanSteps(content);
        var pc = _configService.Current.PlanConfirmation ?? new PlanConfirmationConfig();
        var requiresConfirm = ComputeRequiresUserConfirmation(content, steps.Count, pc);
        var meta = new PlanMeta
        {
            Id = planId,
            Title = title.Length > 80 ? title[..80] + "..." : title,
            Status = "draft",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
            CreatedBy = createdBy,
            CreatedByDisplayName = createdByDisplayName,
            RequiresUserConfirmation = requiresConfirm
        };
        await _store.SaveAsync(planId, content, meta, ct).ConfigureAwait(false);
        _logger.LogInformation("create_plan: saved planId={PlanId} title={Title} requiresUserConfirmation={Requires}", planId, meta.Title, requiresConfirm);
        var confirmHint = requiresConfirm ? " 需用户确认后再执行。" : " 可直接执行。";
        return $"[计划已生成] planId={planId}，标题：{meta.Title}。{confirmHint} 可在计划页查看与编辑，或选择该计划后按步骤执行。";
    }

    [KernelFunction("get_plan")]
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

    [KernelFunction("update_plan")]
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
        await _store.SaveAsync(planId, content ?? "", meta, ct).ConfigureAwait(false);
        _logger.LogInformation("update_plan: planId={PlanId}", planId);
        return "[计划已更新]";
    }

    [KernelFunction("execute_plan_step")]
    [Description("读取计划中指定步骤的内容并返回给你执行。stepIndex 从 1 开始。请在拿到步骤内容后使用你的工具完成该步骤。")]
    public async Task<string> ExecutePlanStepAsync(
        [Description("计划 ID")] string planId,
        [Description("步骤序号，从 1 开始")] int stepIndex = 1,
        CancellationToken ct = default)
    {
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

        return $"[计划步骤 {stepIndex}/{totalSteps}] 请使用你的工具完成以下步骤，完成后简要说明做了什么：\n\n{stepContent}";
    }

    [KernelFunction("complete_plan")]
    [Description("将计划标记为已完成。在全部步骤执行完毕后调用。")]
    public async Task<string> CompletePlanAsync(
        [Description("计划 ID")] string planId,
        CancellationToken ct = default)
    {
        var result = await _store.GetAsync(planId, ct).ConfigureAwait(false);
        if (result == null)
            return $"[未找到计划] planId={planId}";
        var meta = result.Value.Meta;
        meta.Status = "done";
        meta.UpdatedAt = DateTimeOffset.UtcNow;
        await _store.SaveAsync(planId, result.Value.Content, meta, ct).ConfigureAwait(false);
        _logger.LogInformation("complete_plan: planId={PlanId}", planId);
        return "[计划已完成]";
    }

    /// <summary>根据后台配置规则计算该计划是否需要用户确认后再执行。</summary>
    private static bool ComputeRequiresUserConfirmation(string content, int stepCount, PlanConfirmationConfig pc)
    {
        if (stepCount > pc.AutoExecuteMaxSteps)
            return true;
        if (pc.RequireConfirmForSensitiveTools && pc.SensitiveToolIds != null && pc.SensitiveToolIds.Count > 0)
        {
            foreach (var id in pc.SensitiveToolIds)
            {
                if (!string.IsNullOrWhiteSpace(id) && content.Contains(id, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }
        return false;
    }

    private static string? ExtractFirstLine(string content)
    {
        var line = content.AsSpan().Trim();
        if (line.Length == 0) return null;
        var firstLine = line.IndexOf('\n') >= 0 ? line[..line.IndexOf('\n')] : line;
        firstLine = firstLine.Trim();
        if (firstLine.StartsWith("# ", StringComparison.Ordinal))
            firstLine = firstLine[2..].Trim();
        return firstLine.Length > 0 ? firstLine.ToString() : null;
    }

}

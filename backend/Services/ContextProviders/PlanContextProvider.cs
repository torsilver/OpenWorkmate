using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using OfficeCopilot.Server.Services.Plan;

namespace OfficeCopilot.Server.Services.ContextProviders;

/// <summary>
/// MAF <see cref="MessageAIContextProvider"/>：注入当前绑定计划（或指定步骤）为额外 system 消息。
/// PlanResult 在 Part2 阶段预加载（供 MergePlanTools 使用），此 Provider 仅负责格式化注入。
/// </summary>
internal sealed class PlanContextProvider : MessageAIContextProvider
{
    private readonly Func<(string Content, PlanMeta Meta)?> _getPlanResult;
    private readonly int? _planCurrentStepIndex;
    private readonly int _planContentMaxChars;

    /// <param name="getPlanResult">延迟取值，避免 Provider 创建时 PlanResult 尚未就绪。</param>
    public PlanContextProvider(
        Func<(string Content, PlanMeta Meta)?> getPlanResult,
        int? planCurrentStepIndex,
        int planContentMaxChars)
    {
        _getPlanResult = getPlanResult;
        _planCurrentStepIndex = planCurrentStepIndex;
        _planContentMaxChars = planContentMaxChars;
    }

    protected override ValueTask<IEnumerable<ChatMessage>> ProvideMessagesAsync(
        InvokingContext context, CancellationToken cancellationToken = default)
    {
        var planResult = _getPlanResult();
        if (planResult is null)
            return new ValueTask<IEnumerable<ChatMessage>>([]);

        var planContent = planResult.Value.Content;
        var stepIndex = _planCurrentStepIndex is > 0 ? _planCurrentStepIndex.Value : 1;
        var stepOnly = PlanStepParser.GetStepAt(planContent, stepIndex);

        string block;
        if (!string.IsNullOrWhiteSpace(stepOnly))
        {
            block = "[当前绑定的计划·第 " + stepIndex + " 步]\n" + stepOnly;
        }
        else
        {
            if (_planContentMaxChars > 0 && planContent.Length > _planContentMaxChars)
                planContent = planContent.AsSpan(0, _planContentMaxChars).ToString() + "\n（前文已截断）";
            block = "[当前绑定的计划]\n" + planContent;
        }

        IEnumerable<ChatMessage> result = [new ChatMessage(ChatRole.System, block)];
        return new ValueTask<IEnumerable<ChatMessage>>(result);
    }
}

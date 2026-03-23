using System.ComponentModel;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;
using OfficeCopilot.Server.Services;

namespace OfficeCopilot.Server.Plugins;

public sealed class UserOptionsPlugin
{
    private readonly UserOptionsManager _userOptionsManager;
    private readonly ILogger<UserOptionsPlugin> _logger;

    public UserOptionsPlugin(UserOptionsManager userOptionsManager, ILogger<UserOptionsPlugin> logger)
    {
        _userOptionsManager = userOptionsManager;
        _logger = logger;
    }

    private static readonly JsonSerializerOptions ParseOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    [KernelFunction("ask_options")]
    [Description("当模型需要用户对候选项进行分步确认时调用。stepsJson 是一个 JSON 数组字符串：每个元素包含 stepId、question、options（optionId、label）。调用后会等待用户在侧边栏依次选择每一步的一个选项，并在最后一次性返回所有 selections。")]
    public async Task<string> AskOptionsAsync(
        [Description("对用户展示的标题")] string title,
        [Description("对用户展示的说明/提示语")] string prompt,
        [Description("JSON 数组字符串：[{stepId,question,options:[{optionId,label}]}...]")] string stepsJson,
        CancellationToken cancellationToken = default)
    {
        var sessionId = SessionContext.GetSessionId();
        if (string.IsNullOrWhiteSpace(sessionId))
            return "[ask_options] 失败：当前无会话，无法弹出候选项选择 UI。";

        if (string.IsNullOrWhiteSpace(stepsJson))
            return "[ask_options] 失败：stepsJson 不能为空。";

        List<AskOptionsStep>? steps;
        try
        {
            steps = JsonSerializer.Deserialize<List<AskOptionsStep>>(stepsJson, ParseOptions);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[ask_options] stepsJson parse failed");
            return $"[ask_options] 失败：stepsJson 不是合法 JSON，错误：{ex.Message}";
        }

        if (steps == null || steps.Count == 0)
            return "[ask_options] 失败：stepsJson 解析到空步骤。";

        foreach (var s in steps)
        {
            if (string.IsNullOrWhiteSpace(s.StepId))
                return "[ask_options] 失败：某一步的 stepId 不能为空。";
            if (string.IsNullOrWhiteSpace(s.Question))
                return $"[ask_options] 失败：stepId={s.StepId} 的 question 不能为空。";
            if (s.Options == null || s.Options.Count == 0)
                return $"[ask_options] 失败：stepId={s.StepId} 的 options 不能为空。";
            foreach (var o in s.Options)
            {
                if (string.IsNullOrWhiteSpace(o.OptionId) || string.IsNullOrWhiteSpace(o.Label))
                    return $"[ask_options] 失败：stepId={s.StepId} 存在无效 options（optionId/label 不能为空）。";
            }
        }

        AskOptionsRequestResult outcome;
        try
        {
            outcome = await _userOptionsManager.RequestOptionsAsync(sessionId, title, prompt, steps, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) // 含 TaskCanceledException（如用户点停止）
        {
            return "[ask_options] 失败：请求已取消（会话中断或停止生成），未完成侧栏选择。";
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[ask_options] RequestOptionsAsync failed");
            return $"[ask_options] 失败：等待用户选择失败，错误：{ex.Message}";
        }

        if (outcome.TimedOut)
            return $"[ask_options] 失败：等待用户选择超时（{UserOptionsManager.AskOptionsWaitSeconds} 秒内未在侧栏完成选择）。请向用户说明可重试，或在对话中直接说明选项。";

        var selectionsJson = JsonSerializer.Serialize(outcome.Selections, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        return $"[ask_options] 已获取 selections：{selectionsJson}";
    }
}


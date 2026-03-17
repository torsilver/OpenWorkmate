namespace OfficeCopilot.Server.Services;

/// <summary>
/// 标记当前是否在 run_subtask 执行栈内，供 ToolStatusFilter 在发送 tool_invocation_* 时设置 isSubtask。
/// </summary>
public static class SubtaskContext
{
    private static readonly AsyncLocal<bool> IsActive = new();

    public static void SetActive(bool active) => IsActive.Value = active;

    public static bool GetIsActive() => IsActive.Value;
}

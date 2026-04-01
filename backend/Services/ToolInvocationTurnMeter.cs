namespace OfficeCopilot.Server.Services;

/// <summary>
/// 主会话单轮对话内，统计「真实进入插件执行」的次数（与 <see cref="OfficeCopilot.Server.Filters.ToolStatusFilter"/> 在调用 next 前递增对齐）。
/// 通过 <see cref="AsyncLocal{T}"/> 沿异步流传播；仅当 <see cref="BeginTurn"/> 后、<see cref="EndTurn"/> 前有效。
/// </summary>
public static class ToolInvocationTurnMeter
{
    private static readonly AsyncLocal<(bool Active, int Count)> State = new();

    public static void BeginTurn() => State.Value = (true, 0);

    public static void EndTurn() => State.Value = (false, 0);

    public static bool IsActive => State.Value.Active;

    /// <summary>在进入内核函数体之前调用（由 ToolStatusFilter 调用）。</summary>
    public static void RecordInvocation()
    {
        var s = State.Value;
        if (!s.Active)
            return;
        State.Value = (true, s.Count + 1);
    }

    public static int GetCount() => State.Value.Active ? State.Value.Count : 0;

    /// <summary>用于同一轮内第二次模型请求前清零，以便分别统计首轮/重试轮工具次数。</summary>
    public static void ResetCount()
    {
        var s = State.Value;
        if (s.Active)
            State.Value = (true, 0);
    }
}

namespace OfficeCopilot.Server.Services.DashScope;

/// <summary>
/// 标记当前异步流是否处于「后台 LLM 调用」（摘要、工具筛选、子任务、技能生成等），供百炼 Handler 决定是否强制 <c>enable_thinking: false</c>。
/// </summary>
internal static class DashScopeCallKindContext
{
    private static readonly AsyncLocal<int> s_backgroundDepth = new();

    internal static bool IsBackground => s_backgroundDepth.Value > 0;

    internal static IDisposable EnterBackground()
    {
        s_backgroundDepth.Value++;
        return new Scope();
    }

    private sealed class Scope : IDisposable
    {
        public void Dispose()
        {
            var d = s_backgroundDepth.Value;
            if (d > 0)
                s_backgroundDepth.Value = d - 1;
        }
    }
}

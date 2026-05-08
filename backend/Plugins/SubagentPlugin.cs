using System.ComponentModel;
using OpenWorkmate.Server;
using OpenWorkmate.Server.Services;
using OpenWorkmate.Server.Services.Subagent;

namespace OpenWorkmate.Server.Plugins;

/// <summary>同会话内子代理：run_subtask 在隔离上下文中执行子任务，仅将最终结果返回主 Agent，避免大量 tool 输出占用主上下文。</summary>
[OpenWorkmatePluginId("Subagent")]
public sealed class SubagentPlugin
{
    private readonly ChatService _chatService;

    public SubagentPlugin(ChatService chatService)
    {
        _chatService = chatService;
    }

    /// <summary>将复杂或多步子任务交给子代理执行，子代理在独立上下文中运行并只返回最终总结，主对话不会塞入中间过程。</summary>
    [ToolFunction("run_subtask")]
    [Description("当需要执行一段相对独立、多步骤或会产出大量中间结果的任务（如深度检索、多轮读文件、批量操作）时，可调用此工具将任务交给子代理。子代理在隔离上下文中完成并只返回最终总结，主对话仅收到该总结。taskDescription 需写清要完成的具体目标。")]
    public async Task<string> RunSubtaskAsync(
        [Description("要子代理完成的任务描述，清晰具体")] string taskDescription,
        [Description("可选约束或补充说明，如格式要求、范围限制")] string? constraints = null,
        CancellationToken cancellationToken = default)
    {
        var sessionId = SessionContext.GetSessionId();
        return await _chatService.RunSubtaskAsync(sessionId ?? "", taskDescription, constraints, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>对齐 Cursor Explore：多读文件/文档/上下文，中间过程隔离，主会话仅收压缩总结。</summary>
    [ToolFunction("run_explore_subtask")]
    [Description(
        "当需要大范围只读探索（多文件检索、通读目录、对照文档/记忆/上下文、必要时在浏览器侧只读取证）且会产生大量中间工具输出时调用。"
        + " 子代理仅暴露探索类工具，完成后只返回一段总结；不要在主会话里直接堆长日志或全文。"
        + " 若以终端长输出或复杂 shell 为主请用 run_cli_subtask；若以页内脚本深度操作为主请用 run_browser_subtask。")]
    public async Task<string> RunExploreSubtaskAsync(
        [Description("探索目标，须写清范围与要回答的问题")] string taskDescription,
        [Description("可选：输出格式、禁止修改的路径等")] string? constraints = null,
        CancellationToken cancellationToken = default)
    {
        var sessionId = SessionContext.GetSessionId();
        return await _chatService.RunSubtaskWithPresetAsync(sessionId ?? "", SubagentBuiltinPreset.Explore, taskDescription, constraints, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>对齐 Cursor Bash：以 CLI 为主，长输出在子上下文消化。</summary>
    [ToolFunction("run_cli_subtask")]
    [Description(
        "当子任务以命令行/终端为主、输出可能很长（构建、测试、日志、批量脚本）时调用；子代理仅暴露 CLI 工具，总结中只保留关键结论。"
        + " 若以读文件/搜代码为主请用 run_explore_subtask；网页 DOM 操作用 run_browser_subtask。")]
    public async Task<string> RunCliSubtaskAsync(
        [Description("要执行的终端侧目标，清晰具体")] string taskDescription,
        [Description("可选：工作目录、允许/禁止的命令范围等")] string? constraints = null,
        CancellationToken cancellationToken = default)
    {
        var sessionId = SessionContext.GetSessionId();
        return await _chatService.RunSubtaskWithPresetAsync(sessionId ?? "", SubagentBuiltinPreset.CliShell, taskDescription, constraints, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>对齐 Cursor Browser：页内脚本与网页取证，DOM 噪音隔离。</summary>
    [ToolFunction("run_browser_subtask")]
    [Description(
        "当子任务以浏览器页内脚本（打开页、执行预置/自定义脚本、DOM 相关）为主、中间结果嘈杂时调用；子代理仅暴露 Browser 工具，总结中只保留与用户目标相关的结论。"
        + " 仅在 Chrome 等已暴露 Browser 工具的端可用；纯本地文件探索请用 run_explore_subtask。")]
    public async Task<string> RunBrowserSubtaskAsync(
        [Description("浏览器侧子任务描述，清晰具体")] string taskDescription,
        [Description("可选：目标 URL、脚本约束等")] string? constraints = null,
        CancellationToken cancellationToken = default)
    {
        var sessionId = SessionContext.GetSessionId();
        return await _chatService.RunSubtaskWithPresetAsync(sessionId ?? "", SubagentBuiltinPreset.Browser, taskDescription, constraints, cancellationToken).ConfigureAwait(false);
    }
}

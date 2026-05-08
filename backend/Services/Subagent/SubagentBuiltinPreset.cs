namespace OpenWorkmate.Server.Services.Subagent;

/// <summary>内置子代理预设：与通用 <c>run_subtask</c> 共用执行管线，通过 system 与工具白名单收窄职责。</summary>
public enum SubagentBuiltinPreset
{
    /// <summary>通用子任务：允许当前端除子任务入口与压缩外的全部已暴露工具。</summary>
    General = 0,

    /// <summary>探索：多读文件/文档/上下文，中间过程隔离；不含 CLI 专属噪音路径。</summary>
    Explore = 1,

    /// <summary>终端：以 CLI 为主，长输出在子上下文消化。</summary>
    CliShell = 2,

    /// <summary>浏览器：仅页内脚本类工具，DOM 噪音隔离（仅 Chrome 等暴露 Browser 的端）。</summary>
    Browser = 3,
}

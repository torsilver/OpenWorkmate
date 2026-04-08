using Microsoft.Extensions.AI;
using OfficeCopilot.Server;
using OfficeCopilot.Server.Services.Plan;

namespace OfficeCopilot.Server.Services.Chat;

/// <summary>主会话单轮流式编排的共享状态：上下文准备与工具阶段写入，主模型流式阶段读取。</summary>
public sealed class StreamChatTurnContext
{
    public required string SessionId { get; init; }
    public required string UserMessage { get; init; }
    public string? KnowledgeBaseId { get; init; }
    public string? Mode { get; init; }
    public string? PlanId { get; init; }
    public int? PlanCurrentStepIndex { get; init; }

    public required SessionState State { get; init; }
    public required SessionManager SessionManager { get; init; }
    public required ContextWindowConfig CtxConfig { get; init; }

    /// <summary>当前绑定的计划内容（若有）。</summary>
    public (string Content, PlanMeta Meta)? PlanResult { get; set; }

    /// <summary>记忆/知识库等阶段产生的用户可见警告（Part1 填充，在 Part2 前由调用方 yield）。</summary>
    public List<string> ContextWarnings { get; } = new();

    public ChatOptions ExecSettings { get; set; } = null!;
    public List<ChatMessage> HistoryToUse { get; set; } = null!;
    public string IdentitySuffix { get; set; } = "";

    /// <summary>本轮 ToolSelection 解析后的工具列表；与 <see cref="ToolsForAgentRound"/> 同步（受限子集或全量）；闲聊门控为否时可为 null。</summary>
    public IReadOnlyList<AITool>? SelectedTools { get; set; }

    /// <summary>本轮绑定到 MAF 主会话的工具列表（含空列表 = 不向模型注册任何工具）。工具阶段末尾赋值；为 null 时 <see cref="MafMainSessionStreamRunner"/> 回退为全量允许工具。</summary>
    public IReadOnlyList<AITool>? ToolsForAgentRound { get; set; }

    /// <summary>当前活动模型开启百炼 <c>enable_search</c> 时，由主会话 <c>BuildHistoryForStreamingTurn</c> 拼入 system 的短提示（抑制「开浏览器页再搜」）；未开启时为 null。</summary>
    public string? EnableSearchSuppressionSuffix { get; set; }
}

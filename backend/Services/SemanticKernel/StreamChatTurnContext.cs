using OfficeCopilot.Server;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel.Connectors.OpenAI;
using OfficeCopilot.Server.Services.Plan;

namespace OfficeCopilot.Server.Services.SemanticKernel;

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
    public required Kernel Kernel { get; init; }
    public required IChatCompletionService Chat { get; init; }
    public required SessionManager SessionManager { get; init; }
    public required ContextWindowConfig CtxConfig { get; init; }

    /// <summary>当前绑定的计划内容（若有）。</summary>
    public (string Content, PlanMeta Meta)? PlanResult { get; set; }

    /// <summary>记忆/知识库等阶段产生的用户可见警告（Part1 填充，在 Part2 前由调用方 yield）。</summary>
    public List<string> ContextWarnings { get; } = new();

    public OpenAIPromptExecutionSettings ExecSettings { get; set; } = null!;
    public ChatHistory HistoryToUse { get; set; } = null!;
    public string IdentitySuffix { get; set; } = "";

    /// <summary>本轮 ToolSelection 解析后的函数列表；全量工具时为 null，工具接地重试时用于 <c>Required</c> 或回退为客户端允许列表。</summary>
    public IReadOnlyList<KernelFunction>? SelectedKernelFunctions { get; set; }
}

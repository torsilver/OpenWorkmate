using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using OfficeCopilot.Server.Services;

namespace OfficeCopilot.Server.Services.DynamicTooling;

/// <summary>
/// 放在 <see cref="FunctionInvokingChatClient"/> **内侧**、直连模型之前。
/// MEAI 在 <see cref="ChatOptions.Clone"/> 时会复制 <c>Tools</c> 列表；动态工具在 <c>activate_tools</c> 后原地刷新
/// <see cref="DynamicToolingTurnState.ToolListMutationTarget"/> 时，外层 <see cref="ChatOptions"/> 可能仍指向旧快照。
/// 这与上游 <c>FunctionInvokingChatClient</c> 对同轮 <c>Tools</c> 更新的处理（dotnet/extensions#7217/#7218）是不同层面：升级 MEAI 后仍建议在每次发往模型的请求前，
/// 用当前 <see cref="DynamicToolingTurnState.ToolListMutationTarget"/> 覆盖 <c>options.Tools</c>，避免 Clone 快照与可变列表不一致。
/// </summary>
public sealed class DynamicToolingChatOptionsSyncChatClient : IChatClient, IDisposable
{
    private readonly IChatClient _inner;
    private readonly ILogger<DynamicToolingChatOptionsSyncChatClient>? _logger;

    public DynamicToolingChatOptionsSyncChatClient(IChatClient inner, ILogger<DynamicToolingChatOptionsSyncChatClient>? logger = null)
    {
        _inner = inner;
        _logger = logger;
    }

    public void Dispose() => (_inner as IDisposable)?.Dispose();

    public object? GetService(Type serviceType, object? serviceKey = null) =>
        _inner.GetService(serviceType, serviceKey);

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default) =>
        _inner.GetResponseAsync(messages, PatchToolsFromDynamicScope(options), cancellationToken);

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default) =>
        _inner.GetStreamingResponseAsync(messages, PatchToolsFromDynamicScope(options), cancellationToken);

    private ChatOptions? PatchToolsFromDynamicScope(ChatOptions? options)
    {
        if (DynamicToolingTurnScope.Current?.ToolListMutationTarget is not { Count: > 0 } tl)
            return options;

        var beforeCount = options?.Tools?.Count;
        options ??= new ChatOptions();
        options.Tools = new List<AITool>(tl);
        _logger?.LogInformation(
            "[DynamicTools] PatchTools applied before model HTTP: mutationTargetCount={MutationCount} previousOptionsToolsCount={PreviousCount} session={SessionId}",
            tl.Count,
            beforeCount,
            SessionContext.GetSessionId() ?? "?");
        return options;
    }
}

using Microsoft.Extensions.AI;

namespace OfficeCopilot.Server.Services.DynamicTooling;

/// <summary>
/// 放在 <see cref="FunctionInvokingChatClient"/> **内侧**、直连模型之前。
/// MEAI 在 <see cref="ChatOptions.Clone"/> 时会复制 <c>Tools</c> 列表；动态工具在 <c>activate_tools</c> 后原地刷新
/// <see cref="DynamicToolingTurnState.ToolListMutationTarget"/> 时，外层 <see cref="ChatOptions"/> 可能仍指向旧快照，导致
/// <c>FunctionInvokingChatClient</c> 在解析 tool_calls 时 <c>FindTool</c> 找不到刚激活的函数。
/// 在每次发往模型的请求前，用当前 <see cref="DynamicToolingTurnState.ToolListMutationTarget"/> 覆盖 <c>options.Tools</c>，
/// 使 <c>FindTool</c> 与请求体中的 tools 一致。
/// </summary>
public sealed class DynamicToolingChatOptionsSyncChatClient : IChatClient, IDisposable
{
    private readonly IChatClient _inner;

    public DynamicToolingChatOptionsSyncChatClient(IChatClient inner) => _inner = inner;

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

    private static ChatOptions? PatchToolsFromDynamicScope(ChatOptions? options)
    {
        if (DynamicToolingTurnScope.Current?.ToolListMutationTarget is not { Count: > 0 } tl)
            return options;

        options ??= new ChatOptions();
        options.Tools = new List<AITool>(tl);
        return options;
    }
}

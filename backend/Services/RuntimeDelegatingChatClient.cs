using Microsoft.Extensions.AI;

namespace OfficeCopilot.Server.Services;

/// <summary>
/// Delegates to <see cref="IChatRuntimeAccessor.GetChatClient()"/> at call time,
/// enabling DI registration before the runtime chat client is available (e.g. for DevUI).
/// Returns a friendly error if the runtime is not yet initialized.
/// </summary>
internal sealed class RuntimeDelegatingChatClient : IChatClient
{
    private readonly IChatRuntimeAccessor _runtime;

    public RuntimeDelegatingChatClient(IChatRuntimeAccessor runtime) => _runtime = runtime;

    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var client = Resolve() ?? throw new InvalidOperationException("Chat runtime not initialized. Configure a model in settings first.");
        return await client.GetResponseAsync(messages, options, cancellationToken).ConfigureAwait(false);
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var client = Resolve() ?? throw new InvalidOperationException("Chat runtime not initialized. Configure a model in settings first.");
        await foreach (var update in client.GetStreamingResponseAsync(messages, options, cancellationToken).ConfigureAwait(false))
            yield return update;
    }

    public object? GetService(Type serviceType, object? serviceKey = null)
    {
        if (serviceType == typeof(IChatClient)) return this;
        return Resolve()?.GetService(serviceType, serviceKey);
    }

    public void Dispose() { }

    private IChatClient? Resolve() => _runtime.GetChatClient();
}

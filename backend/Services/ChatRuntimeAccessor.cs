using Microsoft.Extensions.AI;

namespace OfficeCopilot.Server.Services;

/// <summary>按会话/端类型提供模型侧 <see cref="AITool"/> 列表。</summary>
public interface IRuntimeTools
{
    IReadOnlyList<AITool> GetAllowedTools(string? clientType, string? sessionId, string? wpsHostKind = null);
}

/// <summary>插件工具集中入口（纯 <see cref="AITool"/> 注册表）。</summary>
public interface IPluginToolRegistry : IRuntimeTools;

/// <summary>
/// 聊天运行时访问：活动模型、<see cref="IChatClient"/> 与过滤后的工具列表。
/// </summary>
public interface IChatRuntimeAccessor : IPluginToolRegistry
{
    string ActiveModelId { get; }
    bool IsReady { get; }

    ToolRegistry ToolRegistry { get; }

    void SetActiveModelId(string activeModelId);
    void SetToolRegistry(ToolRegistry registry);

    /// <summary>注册 MEAI 直连 <see cref="IChatClient"/> 实例（按模型条目 id 键入）。</summary>
    void SetChatClients(IReadOnlyDictionary<string, IChatClient> clients);

    /// <summary>当前活动模型对应的 <see cref="IChatClient"/>（从直连字典解析）。</summary>
    IChatClient? GetChatClient();

    /// <summary>按服务 id 解析聊天客户端；<paramref name="serviceId"/> 为空时使用 <see cref="ActiveModelId"/>。</summary>
    IChatClient? GetChatClient(string? serviceId);

    /// <summary>应用级 <see cref="IServiceProvider"/>，供 MAF Agent 构造使用。</summary>
    IServiceProvider GetPluginServices();
}

/// <inheritdoc cref="IChatRuntimeAccessor" />
public sealed class ChatRuntimeAccessor : IChatRuntimeAccessor
{
    private readonly IServiceProvider _appServices;
    private volatile string _activeModelId = "";
    private volatile ToolRegistry _toolRegistry = new();
    private volatile IReadOnlyDictionary<string, IChatClient> _chatClients = new Dictionary<string, IChatClient>();

    public ChatRuntimeAccessor(IServiceProvider appServices)
    {
        _appServices = appServices;
    }

    public string ActiveModelId => _activeModelId;
    public bool IsReady => _chatClients.Count > 0;
    public ToolRegistry ToolRegistry => _toolRegistry;

    public void SetActiveModelId(string activeModelId) => _activeModelId = activeModelId ?? "";

    public void SetToolRegistry(ToolRegistry registry) => _toolRegistry = registry;

    public void SetChatClients(IReadOnlyDictionary<string, IChatClient> clients) => _chatClients = clients;

    public IChatClient? GetChatClient() => GetChatClient(null);

    public IChatClient? GetChatClient(string? serviceId)
    {
        var id = (serviceId ?? "").Trim();
        if (string.IsNullOrEmpty(id))
            id = (_activeModelId ?? "").Trim();
        if (!string.IsNullOrEmpty(id) && _chatClients.TryGetValue(id, out var client))
            return client;
        var dict = _chatClients;
        foreach (var kv in dict)
            return kv.Value;
        return null;
    }

    /// <inheritdoc />
    public IReadOnlyList<AITool> GetAllowedTools(string? clientType, string? sessionId, string? wpsHostKind = null) =>
        _toolRegistry.GetAllowedTools(clientType, sessionId, wpsHostKind);

    public IServiceProvider GetPluginServices() => _appServices;
}

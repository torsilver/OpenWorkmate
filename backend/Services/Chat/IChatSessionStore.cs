using Microsoft.Extensions.AI;

namespace OfficeCopilot.Server.Services.Chat;

/// <summary>Chrome 等端「历史对话」列表与 transcript 持久化（SQLite）；内存会话过期后仍可从数据库恢复。</summary>
public interface IChatSessionStore
{
    /// <summary>将当前 <see cref="ChatMessage"/> 历史覆盖写入数据库（不含 system 快照）。</summary>
    Task SaveFromHistoryAsync(string sessionId, IReadOnlyList<ChatMessage> history, string? agentProfileId = null, CancellationToken ct = default);

    /// <summary>读取已保存的展示用消息；无记录时返回 null。</summary>
    Task<IReadOnlyList<ChatSessionMessageDto>?> GetMessagesAsync(string sessionId, CancellationToken ct = default);

    /// <summary>同步读取 transcript（供内存无会话时从 SQLite 灌入 <c>ChatService</c> 的 History）。</summary>
    IReadOnlyList<ChatSessionMessageDto>? TryGetPersistedMessages(string sessionId);

    /// <summary>按更新时间降序分页列出会话元数据；<paramref name="agentProfileId"/> 非空时仅返回该 Agent 的会话。</summary>
    Task<(IReadOnlyList<ChatSessionListItemDto> Items, bool HasMore)> ListAsync(int skip, int take, string? agentProfileId = null, CancellationToken ct = default);

    /// <summary>删除会话及其消息，并返回是否曾存在记录。</summary>
    Task<bool> TryDeleteAsync(string sessionId, CancellationToken ct = default);
}

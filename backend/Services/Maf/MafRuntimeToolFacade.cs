using Microsoft.Extensions.AI;

namespace OfficeCopilot.Server.Services.Maf;

/// <summary>
/// 主会话 MAF 路径的工具列表统一入口（阶段 3：后续可替换为无 Kernel 的纯注册表，而调用方仍只依赖本类）。
/// 当前委托 <see cref="IChatRuntimeAccessor.GetAllowedTools"/>。
/// </summary>
public static class MafRuntimeToolFacade
{
    public static IReadOnlyList<AITool> GetToolsForSession(IChatRuntimeAccessor runtime, string? clientType, string? sessionId) =>
        runtime.GetAllowedTools(clientType, sessionId);
}

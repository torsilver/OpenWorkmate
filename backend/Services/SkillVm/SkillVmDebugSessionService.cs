using System.Collections.Concurrent;

namespace OfficeCopilot.Server.Services.SkillVm;

/// <summary>按会话保存 Skill VM 调试标志（模式 B）。</summary>
public sealed class SkillVmDebugSessionService
{
    private readonly ConcurrentDictionary<string, SkillVmDebugFlags> _flags = new(StringComparer.OrdinalIgnoreCase);

    public SkillVmDebugFlags GetFlags(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId)) return new SkillVmDebugFlags();
        return _flags.GetOrAdd(sessionId.Trim(), _ => new SkillVmDebugFlags());
    }

    public void SetFlags(string sessionId, SkillVmDebugFlags flags)
    {
        if (string.IsNullOrWhiteSpace(sessionId)) return;
        _flags[sessionId.Trim()] = flags;
    }
}

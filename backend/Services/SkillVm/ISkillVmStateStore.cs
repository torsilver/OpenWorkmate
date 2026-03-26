namespace OfficeCopilot.Server.Services.SkillVm;

public interface ISkillVmStateStore
{
    SkillVmState GetOrCreate(string sessionId, string activeSkillId, string initialSegmentId);

    bool TryGet(string sessionId, out SkillVmState? state);

    void Update(string sessionId, SkillVmState state);

    void Clear(string sessionId);

    /// <summary>将状态持久化到磁盘（若实现支持）。</summary>
    void Persist(string sessionId, SkillVmState state);

    /// <summary>尝试从磁盘恢复。</summary>
    bool TryLoadPersisted(string sessionId, out SkillVmState? state);
}

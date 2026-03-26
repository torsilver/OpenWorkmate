using OfficeCopilot.Server.Services;

namespace OfficeCopilot.Server.Services.SkillVm;

/// <summary>
/// 构建与 <see cref="ChatService"/> 中 Skill VM 注入块一致的预览（供调试 API / 面板「内存」观测）。
/// </summary>
public static class SkillVmInjectionPreviewBuilder
{
    /// <returns>完整注入块、估计 token（粗略：字符/4）。</returns>
    public static (string injectionBlock, int estimatedTokens) Build(
        SkillVmState vmState,
        SkillService skills,
        ConfigService config)
    {
        var segText = skills.GetSkillVmSegmentText(vmState.ActiveSkillId, vmState.CurrentSegmentId) ?? "";
        var maxSeg = config.Current.SkillVm?.MaxSegmentChars ?? 0;
        if (maxSeg <= 0) maxSeg = 12000;
        var skill = skills.TryGetSkillById(vmState.ActiveSkillId);
        var segDef = skill?.VmManifest?.Segments.FirstOrDefault(s =>
            string.Equals(s.Id, vmState.CurrentSegmentId, StringComparison.OrdinalIgnoreCase));
        if (segDef?.MaxChars is > 0 && segDef.MaxChars.Value < maxSeg)
            maxSeg = segDef.MaxChars.Value;
        if (segText.Length > maxSeg)
            segText = segText.AsSpan(0, maxSeg).ToString() + "\n…（已截断）";

        var snap = "[Skill VM]\n技能=" + vmState.ActiveSkillId + " 当前段=" + vmState.CurrentSegmentId
            + (vmState.Finished ? " （已完成）" : "")
            + (vmState.Paused ? " （已暂停）" : "")
            + "\n请使用 skill_step 工具推进（next/goto/finish/return/pause）。";
        if (vmState.CompletedSegmentIds.Count > 0)
            snap += "\n已完成段：" + string.Join(", ", vmState.CompletedSegmentIds);

        var block = snap + "\n\n[当前段]\n" + segText;
        var est = Math.Max(1, block.Length / 4);
        return (block, est);
    }
}

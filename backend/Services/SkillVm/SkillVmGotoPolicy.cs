namespace OfficeCopilot.Server.Services.SkillVm;

/// <summary>
/// goto 白名单与段存在性校验（与 <see cref="Plugins.SkillVmPlugin"/> 一致，供 CLI 静态检查复用）。
/// </summary>
public static class SkillVmGotoPolicy
{
    public static bool SegmentExists(SkillVmManifest m, string segmentId) =>
        m.Segments.Any(s => string.Equals(s.Id, segmentId, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// 是否允许从当前技能状态跳转到目标技能/段。
    /// </summary>
    public static bool IsGotoAllowed(
        SkillVmManifest? fromManifest,
        SkillVmManifest toManifest,
        string activeSkillId,
        string targetSkillId,
        string targetSegmentId)
    {
        if (string.Equals(activeSkillId, targetSkillId, StringComparison.OrdinalIgnoreCase))
            return SegmentExists(toManifest, targetSegmentId);

        var list = fromManifest?.AllowedGotoTargets;
        var key = targetSkillId + ":" + targetSegmentId;
        if (list is { Count: > 0 })
            return list.Any(x => string.Equals((x ?? "").Trim(), key, StringComparison.OrdinalIgnoreCase));

        return SegmentExists(toManifest, targetSegmentId);
    }
}

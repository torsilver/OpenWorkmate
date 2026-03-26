using System.Text.Json;
using OfficeCopilot.Server.Services.SkillVm;

namespace SkillDebugger.Cli;

/// <summary>确定性 VM 步进（与 <see cref="OfficeCopilot.Server.Plugins.SkillVmPlugin"/> 对齐，无会话落盘）。</summary>
public sealed class VmStepRunner
{
    private readonly SkillDebuggerSkillStore _store;
    private readonly int _maxSegmentChars;

    public VmStepRunner(SkillDebuggerSkillStore store, int maxSegmentChars = 12000)
    {
        _store = store;
        _maxSegmentChars = maxSegmentChars <= 0 ? 12000 : maxSegmentChars;
    }

    public string ApplyNext(SkillVmState state)
    {
        var skill = _store.TryGet(state.ActiveSkillId);
        var manifest = skill?.Manifest;
        if (manifest == null)
            return "[错误] 当前技能没有有效的 VM manifest。";

        var nextId = SkillVmSegmentContent.GetNextSegmentId(manifest, state.CurrentSegmentId);
        if (string.IsNullOrEmpty(nextId))
        {
            state.Finished = true;
            return "[SkillVM] 已到达最后一段，自动完成。";
        }

        state.CompletedSegmentIds.Add(state.CurrentSegmentId);
        state.CurrentSegmentId = nextId;
        return FormatSegmentMessage(skill!, state.ActiveSkillId, state.CurrentSegmentId);
    }

    public string ApplyGoto(SkillVmState state, string? targetSkillId, string? targetSegmentId)
    {
        if (string.IsNullOrWhiteSpace(targetSegmentId))
            return "[错误] goto 需要 targetSegmentId。";

        var toSkillId = string.IsNullOrWhiteSpace(targetSkillId) ? state.ActiveSkillId : targetSkillId.Trim();
        var fromSkill = _store.TryGet(state.ActiveSkillId);
        var toSkill = _store.TryGet(toSkillId);
        if (toSkill?.Manifest == null)
            return "[错误] 目标技能不存在或未配置 skill.manifest.json。";

        if (!SkillVmGotoPolicy.SegmentExists(toSkill.Manifest, targetSegmentId!))
            return "[错误] 目标段不存在于 manifest。";

        if (!SkillVmGotoPolicy.IsGotoAllowed(fromSkill?.Manifest, toSkill.Manifest, state.ActiveSkillId, toSkillId, targetSegmentId!))
            return "[错误] 不允许的 goto 目标（白名单或跨技能限制）。";

        var sameSkill = string.Equals(state.ActiveSkillId, toSkillId, StringComparison.OrdinalIgnoreCase);
        if (!sameSkill)
        {
            var resume = SkillVmSegmentContent.GetNextSegmentId(fromSkill!.Manifest!, state.CurrentSegmentId);
            state.Stack.Add(new SkillVmStackFrame
            {
                SkillId = state.ActiveSkillId,
                SegmentId = resume ?? state.CurrentSegmentId,
                ReturnSegmentId = state.CurrentSegmentId
            });
            state.ActiveSkillId = toSkillId;
            state.CurrentSegmentId = targetSegmentId!.Trim();
        }
        else
        {
            state.CurrentSegmentId = targetSegmentId!.Trim();
        }

        return FormatSegmentMessage(toSkill, state.ActiveSkillId, state.CurrentSegmentId);
    }

    public string ApplyReturn(SkillVmState state)
    {
        if (state.Stack.Count == 0)
            return "[错误] 调用栈为空，无法 return。";

        var frame = state.Stack[^1];
        state.Stack.RemoveAt(state.Stack.Count - 1);
        state.ActiveSkillId = frame.SkillId;
        state.CurrentSegmentId = frame.SegmentId;
        var skill = _store.TryGet(state.ActiveSkillId);
        if (skill == null) return "[错误] 返回后技能不存在。";
        return FormatSegmentMessage(skill, state.ActiveSkillId, state.CurrentSegmentId);
    }

    private string FormatSegmentMessage(SkillInfo skill, string skillId, string segmentId)
    {
        var text = skill.GetSegmentText(segmentId) ?? "";
        var max = _maxSegmentChars;
        var segDef = skill.Manifest.Segments.FirstOrDefault(s =>
            string.Equals(s.Id, segmentId, StringComparison.OrdinalIgnoreCase));
        if (segDef?.MaxChars is > 0 && segDef.MaxChars.Value < max)
            max = segDef.MaxChars.Value;
        if (text.Length > max)
            text = text.AsSpan(0, max).ToString() + "\n…（已按 maxSegmentChars 截断）";

        return "[SkillVM] 当前技能=" + skillId + " 段=" + segmentId + (string.IsNullOrWhiteSpace(text) ? "" : "\n\n" + text);
    }

    public static SkillVmState CreateInitialState(string sessionId, SkillInfo skill)
    {
        var first = SkillVmSegmentContent.GetFirstSegmentId(skill.Manifest);
        if (string.IsNullOrEmpty(first))
            throw new InvalidOperationException("manifest 中无有效段。");
        return new SkillVmState
        {
            SessionId = sessionId,
            ActiveSkillId = skill.Id,
            CurrentSegmentId = first,
            Stack = new List<SkillVmStackFrame>(),
            Variables = new Dictionary<string, JsonElement>(),
            CompletedSegmentIds = new List<string>()
        };
    }
}

using System.ComponentModel;
using Microsoft.SemanticKernel;
using OfficeCopilot.Server.Services;
using OfficeCopilot.Server.Services.SkillVm;

namespace OfficeCopilot.Server.Plugins;

/// <summary>脚本化 Skill VM：通过 <c>skill_step</c> 推进段、跳转、结束或暂停。</summary>
public sealed class SkillVmPlugin
{
    private readonly SkillService _skills;
    private readonly ISkillVmStateStore _store;
    private readonly ConfigService _config;
    private readonly SkillVmDebugSessionService _debug;
    private readonly ILogger<SkillVmPlugin>? _logger;

    public SkillVmPlugin(
        SkillService skills,
        ISkillVmStateStore store,
        ConfigService config,
        SkillVmDebugSessionService debug,
        ILogger<SkillVmPlugin>? logger = null)
    {
        _skills = skills;
        _store = store;
        _config = config;
        _debug = debug;
        _logger = logger;
    }

    [KernelFunction("skill_step")]
    [Description(
        "脚本化 Skill 分段执行：推进或跳转。action：next=下一段；goto=跳转到 targetSkillId/targetSegmentId；finish=标记完成；return=从子技能返回；pause=暂停并落盘。仅当会话处于 Skill VM 模式且已绑定技能时有效。")]
    public Task<string> SkillStepAsync(
        [Description("next | goto | finish | return | pause")] string action,
        [Description("goto 时目标技能 Id，默认同当前技能")] string? targetSkillId = null,
        [Description("goto 时目标段 Id")] string? targetSegmentId = null,
        [Description("可选说明")] string? reason = null,
        CancellationToken cancellationToken = default)
    {
        var sessionId = SessionContext.GetSessionId();
        if (string.IsNullOrEmpty(sessionId))
            return Task.FromResult("[错误] 未关联会话，无法执行 skill_step。");

        if (!_store.TryGet(sessionId, out var state) || state == null)
            return Task.FromResult("[错误] 当前会话没有 Skill VM 状态，请先以 skillVmMode 与 skillVmSkillId 开启对话。");

        if (state.Finished)
            return Task.FromResult("[提示] Skill VM 已处于完成状态。");

        if (_config.Current.SkillVm is { Enabled: false })
            return Task.FromResult("[错误] Skill VM 已在配置中关闭。");

        var act = (action ?? "").Trim().ToLowerInvariant();
        try
        {
            switch (act)
            {
                case "next":
                    return Task.FromResult(ApplyNext(state, sessionId));
                case "goto":
                    return Task.FromResult(ApplyGoto(state, sessionId, targetSkillId, targetSegmentId));
                case "finish":
                    state.Finished = true;
                    state.Paused = false;
                    _store.Update(sessionId, state);
                    _store.Persist(sessionId, state);
                    return Task.FromResult("[SkillVM] 已标记完成（finish）。");
                case "return":
                    return Task.FromResult(ApplyReturn(state, sessionId));
                case "pause":
                    state.Paused = true;
                    _store.Update(sessionId, state);
                    _store.Persist(sessionId, state);
                    return Task.FromResult("[SkillVM] 已暂停并保存检查点（pause）。");
                default:
                    return Task.FromResult(
                        "[错误] 无效的 action，请使用 next、goto、finish、return 或 pause。");
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "skill_step failed");
            return Task.FromResult("[错误] skill_step 执行异常：" + ex.Message);
        }
    }

    private string ApplyNext(SkillVmState state, string sessionId)
    {
        var skill = _skills.TryGetSkillById(state.ActiveSkillId);
        var manifest = skill?.VmManifest;
        if (manifest == null)
            return "[错误] 当前技能没有有效的 VM manifest。";

        var nextId = SkillVmSegmentContent.GetNextSegmentId(manifest, state.CurrentSegmentId);
        if (string.IsNullOrEmpty(nextId))
        {
            state.Finished = true;
            _store.Update(sessionId, state);
            _store.Persist(sessionId, state);
            MaybePauseAfterSkillStep(sessionId, state);
            return "[SkillVM] 已到达最后一段，自动完成。";
        }

        state.CompletedSegmentIds.Add(state.CurrentSegmentId);
        state.CurrentSegmentId = nextId;
        _store.Update(sessionId, state);
        _store.Persist(sessionId, state);
        MaybePauseAfterSkillStep(sessionId, state);
        return FormatSegmentMessage(skill!, state.ActiveSkillId, nextId);
    }

    private string ApplyGoto(SkillVmState state, string sessionId, string? targetSkillId, string? targetSegmentId)
    {
        if (string.IsNullOrWhiteSpace(targetSegmentId))
            return "[错误] goto 需要 targetSegmentId。";

        var toSkillId = string.IsNullOrWhiteSpace(targetSkillId) ? state.ActiveSkillId : targetSkillId.Trim();
        var fromSkill = _skills.TryGetSkillById(state.ActiveSkillId);
        var toSkill = _skills.TryGetSkillById(toSkillId);
        if (toSkill?.VmManifest == null)
            return "[错误] 目标技能不存在或未配置 skill.manifest.json。";

        if (!SkillVmGotoPolicy.SegmentExists(toSkill.VmManifest!, targetSegmentId!))
            return "[错误] 目标段不存在于 manifest。";

        if (!SkillVmGotoPolicy.IsGotoAllowed(fromSkill?.VmManifest, toSkill.VmManifest!, state.ActiveSkillId, toSkillId, targetSegmentId!))
            return "[错误] 不允许的 goto 目标（白名单或跨技能限制）。";

        var sameSkill = string.Equals(state.ActiveSkillId, toSkillId, StringComparison.OrdinalIgnoreCase);
        if (!sameSkill)
        {
            var resume = SkillVmSegmentContent.GetNextSegmentId(fromSkill!.VmManifest!, state.CurrentSegmentId);
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

        _store.Update(sessionId, state);
        _store.Persist(sessionId, state);
        MaybePauseAfterSkillStep(sessionId, state);
        return FormatSegmentMessage(toSkill, state.ActiveSkillId, state.CurrentSegmentId);
    }

    private string ApplyReturn(SkillVmState state, string sessionId)
    {
        if (state.Stack.Count == 0)
            return "[错误] 调用栈为空，无法 return。";

        var frame = state.Stack[^1];
        state.Stack.RemoveAt(state.Stack.Count - 1);
        state.ActiveSkillId = frame.SkillId;
        state.CurrentSegmentId = frame.SegmentId;
        _store.Update(sessionId, state);
        _store.Persist(sessionId, state);
        MaybePauseAfterSkillStep(sessionId, state);
        var skill = _skills.TryGetSkillById(state.ActiveSkillId);
        if (skill == null) return "[错误] 返回后技能不存在。";
        return FormatSegmentMessage(skill, state.ActiveSkillId, state.CurrentSegmentId);
    }

    private void MaybePauseAfterSkillStep(string sessionId, SkillVmState state)
    {
        if (!_debug.GetFlags(sessionId).PauseAfterSkillStep) return;
        state.Paused = true;
        _store.Update(sessionId, state);
        _store.Persist(sessionId, state);
    }

    private string FormatSegmentMessage(SkillDefinition skill, string skillId, string segmentId)
    {
        var text = _skills.GetSkillVmSegmentText(skillId, segmentId) ?? "";
        var max = _config.Current.SkillVm?.MaxSegmentChars ?? 0;
        if (max <= 0) max = 12000;
        var segDef = skill.VmManifest?.Segments.FirstOrDefault(s =>
            string.Equals(s.Id, segmentId, StringComparison.OrdinalIgnoreCase));
        if (segDef?.MaxChars is > 0 && segDef.MaxChars.Value < max)
            max = segDef.MaxChars.Value;
        if (text.Length > max)
            text = text.AsSpan(0, max).ToString() + "\n…（已按 maxSegmentChars 截断）";

        return "[SkillVM] 当前技能=" + skillId + " 段=" + segmentId + (string.IsNullOrWhiteSpace(text) ? "" : "\n\n" + text);
    }
}

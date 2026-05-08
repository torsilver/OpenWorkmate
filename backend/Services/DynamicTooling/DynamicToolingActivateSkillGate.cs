namespace OpenWorkmate.Server.Services.DynamicTooling;

/// <summary>
/// 项目内置：本轮一旦调用过 <c>search_available_tools</c> 且存在已启用用户技能，
/// 则 <c>activate_tools</c> 前须至少调用过一次 <c>search_available_skills</c>（不强制 load）。
/// </summary>
public static class DynamicToolingActivateSkillGate
{
    /// <summary>若应拦截 <c>activate_tools</c>，返回 true 及给用户/模型的说明文案。</summary>
    public static bool ShouldBlock(DynamicToolingTurnState state, out string message)
    {
        message = "";
        if (!DynamicToolingConstants.RequireSkillSearchBeforeActivateAfterToolSearch)
            return false;
        if (state.SkillCatalog.Entries.Count == 0)
            return false;
        if (state.SearchInvocationCount < 1)
            return false;
        if (state.SkillSearchInvocationCount >= 1)
            return false;

        message =
            "[activate_tools] 本轮已使用过 search_available_tools，按产品规则须先至少调用一次 search_available_skills（可填任务关键词或空 query），再 activate_tools。"
            + " 该步仅扫描技能目录；是否 select_skill_for_turn / load_user_skill_instructions 仍由任务决定。";
        return true;
    }
}

namespace OfficeCopilot.Server.Services.DynamicTooling;

public static class DynamicToolingConstants
{
    public const string SearchFunctionName = "search_available_tools";
    public const string ActivateFunctionName = "activate_tools";
    /// <summary>渐进式技能正文/资源加载；不进工具检索索引，与业务工具发现分离。</summary>
    public const string LoadUserSkillInstructionsFunctionName = "load_user_skill_instructions";

    public const string SearchAvailableSkillsFunctionName = "search_available_skills";
    public const string SelectSkillForTurnFunctionName = "select_skill_for_turn";

    /// <summary>每用户轮内 <c>search_available_skills</c> 调用次数上限（非配置项）。</summary>
    public const int MaxSkillSearchPerTurnDefault = 12;

    /// <summary>每用户轮内 <c>select_skill_for_turn</c> 调用次数上限（非配置项）。</summary>
    public const int MaxSkillSelectPerTurnDefault = 8;

    public static readonly HashSet<string> MetaFunctionNames = new(StringComparer.OrdinalIgnoreCase)
    {
        SearchFunctionName,
        ActivateFunctionName,
        SearchAvailableSkillsFunctionName,
        SelectSkillForTurnFunctionName,
        LoadUserSkillInstructionsFunctionName
    };

    /// <summary>为 false 时可关闭「工具检索后须先技能检索再激活」；默认 true，非用户配置项。</summary>
    public static bool RequireSkillSearchBeforeActivateAfterToolSearch { get; set; } = true;
}

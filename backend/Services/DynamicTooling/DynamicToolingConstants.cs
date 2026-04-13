namespace OfficeCopilot.Server.Services.DynamicTooling;

public static class DynamicToolingConstants
{
    public const string SearchFunctionName = "search_available_tools";
    public const string ActivateFunctionName = "activate_tools";
    /// <summary>渐进式技能正文/资源加载；不进工具检索索引，与业务工具发现分离。</summary>
    public const string LoadUserSkillInstructionsFunctionName = "load_user_skill_instructions";

    public static readonly HashSet<string> MetaFunctionNames = new(StringComparer.OrdinalIgnoreCase)
    {
        SearchFunctionName,
        ActivateFunctionName,
        LoadUserSkillInstructionsFunctionName
    };
}

namespace OfficeCopilot.Server.Services.DynamicTooling;

public static class DynamicToolingConstants
{
    public const string SearchFunctionName = "search_available_tools";
    public const string ActivateFunctionName = "activate_tools";

    public static readonly HashSet<string> MetaFunctionNames = new(StringComparer.OrdinalIgnoreCase)
    {
        SearchFunctionName,
        ActivateFunctionName
    };
}

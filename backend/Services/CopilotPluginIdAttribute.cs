namespace OfficeCopilot.Server.Services;

/// <summary>
/// 标记内置插件类在 <see cref="ToolRegistry"/> 中的插件 Id（与 <c>DisabledBuiltInPlugins</c> 逐项小写比对）。
/// </summary>
[AttributeUsage(AttributeTargets.Class)]
public sealed class CopilotPluginIdAttribute : Attribute
{
    public string Id { get; }

    public CopilotPluginIdAttribute(string id)
    {
        Id = id ?? throw new ArgumentNullException(nameof(id));
    }
}

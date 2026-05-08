namespace OpenWorkmate.Server.Services;

/// <summary>
/// 标记内置插件类在 <see cref="ToolRegistry"/> 中的插件 Id（与 <c>DisabledBuiltInPlugins</c> 逐项小写比对）。
/// </summary>
[AttributeUsage(AttributeTargets.Class)]
public sealed class OpenWorkmatePluginIdAttribute : Attribute
{
    public string Id { get; }

    public OpenWorkmatePluginIdAttribute(string id)
    {
        Id = id ?? throw new ArgumentNullException(nameof(id));
    }
}

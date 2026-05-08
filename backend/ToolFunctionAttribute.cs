namespace OpenWorkmate.Server;

/// <summary>
/// Marks a method as a tool function for AI model invocation.
/// Replaces <c>[KernelFunction]</c> from Semantic Kernel.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class ToolFunctionAttribute : Attribute
{
    public string? Name { get; }

    public ToolFunctionAttribute() { }

    public ToolFunctionAttribute(string name) => Name = name;
}

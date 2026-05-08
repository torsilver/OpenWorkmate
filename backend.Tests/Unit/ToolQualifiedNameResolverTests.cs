using Microsoft.Extensions.AI;
using OpenWorkmate.Server.Services;
using OpenWorkmate.Server.Services.ToolInvocation;
using Xunit;

namespace backend.Tests.Unit;

public class ToolQualifiedNameResolverTests
{
    private static AITool StubFn(string name) =>
        AIFunctionFactory.Create(
            () => Task.FromResult<object?>("ok"),
            new AIFunctionFactoryOptions { Name = name, Description = "test" });

    private static ToolRegistry CreateRegistry()
    {
        var reg = new ToolRegistry();
        reg.Register("Word", "word_document_create", StubFn("word_document_create"));
        reg.Register("Browser", "run_builtin_page_script", StubFn("run_builtin_page_script"));
        reg.Register("Excel", "excel_range_read", StubFn("excel_range_read"));
        return reg;
    }

    [Fact]
    public void Qualified_OneDot_ResolvesToBareName()
    {
        var reg = CreateRegistry();
        Assert.True(ToolQualifiedNameResolver.TryResolve(reg, "Word.word_document_create", out var p, out var bare, out var tool));
        Assert.Equal("Word", p);
        Assert.Equal("word_document_create", bare);
        Assert.NotNull(tool);
        Assert.Equal("word_document_create", tool!.Name);
    }

    [Fact]
    public void BareName_ResolvesCaseInsensitive()
    {
        var reg = CreateRegistry();
        Assert.True(ToolQualifiedNameResolver.TryResolve(reg, "RUN_BUILTIN_PAGE_SCRIPT", out var p, out var bare, out _));
        Assert.Equal("Browser", p);
        Assert.Equal("RUN_BUILTIN_PAGE_SCRIPT", bare);
    }

    [Fact]
    public void Unknown_ReturnsFalse()
    {
        var reg = CreateRegistry();
        Assert.False(ToolQualifiedNameResolver.TryResolve(reg, "nope", out _, out _, out var tool));
        Assert.Null(tool);
        Assert.False(ToolQualifiedNameResolver.TryResolve(reg, "Word.nope", out _, out _, out tool));
        Assert.Null(tool);
    }

    [Fact]
    public void MultipleDots_ReturnsFalse()
    {
        var reg = CreateRegistry();
        Assert.False(ToolQualifiedNameResolver.TryResolve(reg, "Word.foo.bar", out _, out _, out _));
    }

    [Fact]
    public void EmptyOrWhitespace_ReturnsFalse()
    {
        var reg = CreateRegistry();
        Assert.False(ToolQualifiedNameResolver.TryResolve(reg, "", out _, out _, out _));
        Assert.False(ToolQualifiedNameResolver.TryResolve(reg, "   ", out _, out _, out _));
        Assert.False(ToolQualifiedNameResolver.TryResolve(reg, null, out _, out _, out _));
    }

    [Fact]
    public void WrongPlugin_ReturnsFalse()
    {
        var reg = CreateRegistry();
        Assert.False(ToolQualifiedNameResolver.TryResolve(reg, "Excel.word_document_create", out _, out _, out _));
    }
}

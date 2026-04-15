using System.Text.Json;
using OfficeCopilot.Server.Plugins;
using Xunit;

namespace backend.Tests.Unit;

public sealed class ExcelWriteRangeDataShapeTests
{
    [Fact]
    public void TryGetJaggedShape_EmptyOuterArray_Ok()
    {
        using var doc = JsonDocument.Parse("[]");
        Assert.True(ExcelWriteRangeDataShape.TryGetJaggedShape(doc.RootElement, out var rows, out var first, out var max, out var uniform));
        Assert.Equal(0, rows);
        Assert.Equal(0, first);
        Assert.Equal(0, max);
        Assert.True(uniform);
    }

    [Fact]
    public void TryGetJaggedShape_Rectangle_Uniform()
    {
        using var doc = JsonDocument.Parse("[[1,2],[3,4]]");
        Assert.True(ExcelWriteRangeDataShape.TryGetJaggedShape(doc.RootElement, out var rows, out var first, out var max, out var uniform));
        Assert.Equal(2, rows);
        Assert.Equal(2, first);
        Assert.Equal(2, max);
        Assert.True(uniform);
    }

    [Fact]
    public void TryGetJaggedShape_Jagged_NotUniform()
    {
        using var doc = JsonDocument.Parse("[[1,2],[3]]");
        Assert.True(ExcelWriteRangeDataShape.TryGetJaggedShape(doc.RootElement, out var rows, out var first, out var max, out var uniform));
        Assert.Equal(2, rows);
        Assert.Equal(2, first);
        Assert.Equal(2, max);
        Assert.False(uniform);
    }

    [Fact]
    public void TryGetJaggedShape_PrimitiveOuter_Invalid()
    {
        using var doc = JsonDocument.Parse("[1,2,3]");
        Assert.False(ExcelWriteRangeDataShape.TryGetJaggedShape(doc.RootElement, out _, out _, out _, out _));
    }

    [Fact]
    public void TryGetJaggedShape_ObjectRoot_Invalid()
    {
        using var doc = JsonDocument.Parse("{\"a\":1}");
        Assert.False(ExcelWriteRangeDataShape.TryGetJaggedShape(doc.RootElement, out _, out _, out _, out _));
    }

    [Theory]
    [InlineData("A1", true)]
    [InlineData("  B2  ", true)]
    [InlineData("A1:B2", false)]
    [InlineData("", false)]
    [InlineData("$A$1", false)]
    public void LooksLikeSingleCellAddress_Heuristic(string address, bool expected)
    {
        Assert.Equal(expected, ExcelWriteRangeDataShape.LooksLikeSingleCellAddress(address));
    }
}

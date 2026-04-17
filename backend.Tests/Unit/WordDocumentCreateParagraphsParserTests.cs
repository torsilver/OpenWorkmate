using System.Text.Json;
using OfficeCopilot.Server.Plugins;
using Xunit;

namespace backend.Tests.Unit;

public class WordDocumentCreateParagraphsParserTests
{
    [Fact]
    public void Parse_Null_ReturnsNull()
    {
        Assert.Null(WordDocumentCreateParagraphsParser.Parse((JsonElement?)null));
    }

    [Fact]
    public void Parse_Array_PreservesOrder()
    {
        var el = JsonSerializer.SerializeToElement(new[] { "a", "b" });
        var arr = WordDocumentCreateParagraphsParser.Parse(el);
        Assert.NotNull(arr);
        Assert.Equal(new[] { "a", "b" }, arr);
    }

    [Fact]
    public void Parse_SingleString_BecomesOneItem()
    {
        var el = JsonSerializer.SerializeToElement("一段");
        var arr = WordDocumentCreateParagraphsParser.Parse(el);
        Assert.NotNull(arr);
        Assert.Single(arr);
        Assert.Equal("一段", arr![0]);
    }

    [Fact]
    public void Parse_ObjectWithNumericKeys_CollectsValues()
    {
        var json = """{"0":"x","1":"y"}""";
        using var doc = JsonDocument.Parse(json);
        var arr = WordDocumentCreateParagraphsParser.Parse(doc.RootElement);
        Assert.NotNull(arr);
        Assert.Equal(new[] { "x", "y" }, arr);
    }

    /// <summary>模型把 JSON 数组字面量塞进 string 字段时，应展开为多段，便于按真实段落落盘。</summary>
    [Fact]
    public void Parse_StringContainingJsonStringArray_ExpandsToMultipleParagraphs()
    {
        var inner = """["## A\nx", "## B\ny"]""";
        var el = JsonSerializer.SerializeToElement(inner);
        var arr = WordDocumentCreateParagraphsParser.Parse(el);
        Assert.NotNull(arr);
        Assert.Equal(2, arr!.Length);
        Assert.Equal("## A\nx", arr[0]);
        Assert.Equal("## B\ny", arr[1]);
    }

    [Fact]
    public void Parse_ArrayWithSingleJsonArrayLiteralString_Expands()
    {
        var inner = """["p1", "p2"]""";
        var el = JsonSerializer.SerializeToElement(new[] { inner });
        var arr = WordDocumentCreateParagraphsParser.Parse(el);
        Assert.NotNull(arr);
        Assert.Equal(new[] { "p1", "p2" }, arr);
    }

    [Fact]
    public void Parse_StringThatIsNotJsonArray_StaysSingleParagraph()
    {
        var el = JsonSerializer.SerializeToElement("[not json array");
        var arr = WordDocumentCreateParagraphsParser.Parse(el);
        Assert.NotNull(arr);
        Assert.Single(arr);
        Assert.Equal("[not json array", arr![0]);
    }
}

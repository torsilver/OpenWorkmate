using OfficeCopilot.Server.Services;
using Xunit;

namespace backend.Tests.Unit;

public sealed class ToolSelectionParseTests
{
    [Fact]
    public void Parse_subcategory_ids_splits_mixed_ascii_and_fullwidth_commas()
    {
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "File", "Word-获取信息", "Word-编辑内容" };
        var raw = "File, Word-获取信息，Word-编辑内容";
        var parsed = ToolSelectionService.ParseSubcategoryIdsFromResponse(raw, ids);
        Assert.Equal(3, parsed.Count);
        Assert.Contains("File", parsed);
        Assert.Contains("Word-获取信息", parsed);
        Assert.Contains("Word-编辑内容", parsed);
    }

    [Fact]
    public void Parse_subcategory_ids_splits_fullwidth_semicolon()
    {
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Word-编辑内容", "CLI" };
        var parsed = ToolSelectionService.ParseSubcategoryIdsFromResponse("Word-编辑内容；CLI", ids);
        Assert.Equal(2, parsed.Count);
    }
}

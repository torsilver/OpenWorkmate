using Microsoft.SemanticKernel.ChatCompletion;
using OfficeCopilot.Server.Services;
using Xunit;

namespace backend.Tests.Unit;

public sealed class ToolSelectionRecallHelperTests
{
    [Theory]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData("chrome", true)]
    [InlineData("Chrome", true)]
    [InlineData("office-word", false)]
    public void IsChromeClient_cases(string? ct, bool expectedChrome)
    {
        Assert.Equal(expectedChrome, ToolSelectionRecallHelper.IsChromeClient(ct));
    }

    [Fact]
    public void HistorySuggestsExcel_when_user_message_has_xlsx()
    {
        var h = new ChatHistory();
        Assert.True(ToolSelectionRecallHelper.HistoryOrMessageSuggestsExcelContext("open foo.xlsx", h));
    }

    [Fact]
    public void HistorySuggestsExcel_from_recent_history()
    {
        var h = new ChatHistory();
        h.AddUserMessage("hello");
        h.AddAssistantMessage("已处理 taskly-excel-test.xlsx 的合并");
        Assert.True(ToolSelectionRecallHelper.HistoryOrMessageSuggestsExcelContext("再来一次", h));
    }

    [Fact]
    public void Merge_adds_Excel_style_when_chrome_and_context_and_valid_id()
    {
        var ids = new List<string> { "File" };
        var valid = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "File", ToolSelectionRecallHelper.ExcelStyleSubcategoryId };
        ToolSelectionRecallHelper.MergeChromeExcelStyleSubcategoryIfNeeded(ids, "chrome", "请处理 report.xlsx", null, valid);
        Assert.Equal(2, ids.Count);
        Assert.Contains(ToolSelectionRecallHelper.ExcelStyleSubcategoryId, ids);
    }

    [Fact]
    public void Merge_skips_when_not_chrome()
    {
        var ids = new List<string> { "File" };
        var valid = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "File", ToolSelectionRecallHelper.ExcelStyleSubcategoryId };
        ToolSelectionRecallHelper.MergeChromeExcelStyleSubcategoryIfNeeded(ids, "office-word", "xlsx", null, valid);
        Assert.Single(ids);
    }

    [Fact]
    public void Merge_idempotent_when_already_has_excel_style()
    {
        var ids = new List<string> { "File", ToolSelectionRecallHelper.ExcelStyleSubcategoryId };
        var valid = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "File", ToolSelectionRecallHelper.ExcelStyleSubcategoryId };
        ToolSelectionRecallHelper.MergeChromeExcelStyleSubcategoryIfNeeded(ids, "chrome", "合并单元格", null, valid);
        Assert.Equal(2, ids.Count);
    }
}

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

    [Fact]
    public void ExcludeCurrentDocument_for_chrome_removes_task_pane_subcategories()
    {
        var list = new List<(string Id, string Description)>
        {
            ("Word-编辑内容", "…"),
            ("CurrentDocument-Word", "…"),
            ("CurrentDocument-Excel", "…"),
        };
        var filtered = ToolSelectionRecallHelper.ExcludeCurrentDocumentSubcategoriesForChrome(list, "chrome");
        Assert.Single(filtered);
        Assert.Equal("Word-编辑内容", filtered[0].Id);
        Assert.DoesNotContain(filtered, x => x.Id.StartsWith("CurrentDocument-", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ExcludeCurrentDocument_for_office_word_unchanged()
    {
        var list = new List<(string Id, string Description)> { ("CurrentDocument-Word", "…") };
        var filtered = ToolSelectionRecallHelper.ExcludeCurrentDocumentSubcategoriesForChrome(list, "office-word");
        Assert.Single(filtered);
        Assert.Equal("CurrentDocument-Word", filtered[0].Id);
    }
}

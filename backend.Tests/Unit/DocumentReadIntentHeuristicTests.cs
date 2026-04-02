using OfficeCopilot.Server.Services;
using Xunit;

namespace backend.Tests.Unit;

public sealed class DocumentReadIntentHeuristicTests
{
    [Theory]
    [InlineData("请 ppt_slide_read：slideIndex=1。", true)]
    [InlineData("请 word_body_read：taskly.docx", true)]
    [InlineData("用 excel_range_read 读 Sheet1!A1", true)]
    [InlineData("请 Word.word_tables_list。", true)]
    [InlineData("请 current_word_read_body", true)]
    [InlineData("请 current_ppt_slide_read：1", true)]
    [InlineData("今天天气怎么样", false)]
    [InlineData("请 excel_range_write：A1，值 1", false)]
    [InlineData("请 word_find_replace", false)]
    [InlineData("用 search_memory 查笔记", false)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData(null, false)]
    public void LikelyRequiresDocumentReadTool_MatchesExpected(string? message, bool expected)
    {
        Assert.Equal(expected, DocumentReadIntentHeuristic.LikelyRequiresDocumentReadTool(message));
    }
}

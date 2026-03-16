using OfficeCopilot.Server.Services;
using Xunit;

namespace backend.Tests.Unit;

public class ClientTypeToolFilterTests
{
    [Fact]
    public void Chrome_AllowsCli_ButNotCurrentDocument()
    {
        Assert.True(ClientTypeToolFilter.IsAllowed("CLI", "run_command", "chrome"));
        Assert.False(ClientTypeToolFilter.IsAllowed("CurrentDocument", "current_word_insert_text", "chrome"));
    }

    [Fact]
    public void OfficeWord_AllowsCurrentDocumentWordFunctions()
    {
        Assert.True(ClientTypeToolFilter.IsAllowed("CurrentDocument", "current_word_insert_text", "office-word"));
        Assert.True(ClientTypeToolFilter.IsAllowed("CurrentDocument", "current_word_read_body", "office-word"));
        Assert.True(ClientTypeToolFilter.IsAllowed("CurrentDocument", "current_run_document_script", "office-word"));
    }

    [Fact]
    public void OfficeWord_DoesNotAllowCurrentDocumentExcelFunctions()
    {
        Assert.False(ClientTypeToolFilter.IsAllowed("CurrentDocument", "current_excel_read_range", "office-word"));
        Assert.False(ClientTypeToolFilter.IsAllowed("CurrentDocument", "current_excel_write_range", "office-word"));
    }

    [Fact]
    public void OfficeExcel_AllowsCurrentDocumentExcelFunctions()
    {
        Assert.True(ClientTypeToolFilter.IsAllowed("CurrentDocument", "current_excel_read_range", "office-excel"));
        Assert.True(ClientTypeToolFilter.IsAllowed("CurrentDocument", "current_excel_write_range", "office-excel"));
        Assert.True(ClientTypeToolFilter.IsAllowed("CurrentDocument", "current_excel_list_sheets", "office-excel"));
    }

    [Fact]
    public void Wps_AllowsAllCurrentDocumentFunctions()
    {
        Assert.True(ClientTypeToolFilter.IsAllowed("CurrentDocument", "current_word_insert_text", "wps"));
        Assert.True(ClientTypeToolFilter.IsAllowed("CurrentDocument", "current_excel_read_range", "wps"));
        Assert.True(ClientTypeToolFilter.IsAllowed("CurrentDocument", "current_ppt_slides_list", "wps"));
        Assert.True(ClientTypeToolFilter.IsAllowed("CurrentDocument", "current_run_document_script", "wps"));
    }

    [Fact]
    public void CommonPlugins_AllowedOnAllClientTypes()
    {
        var clientTypes = new[] { "chrome", "office-word", "office-excel", "office-powerpoint", "wps" };
        foreach (var ct in clientTypes)
        {
            Assert.True(ClientTypeToolFilter.IsAllowed("Memory", "recall", ct), $"Memory should be allowed for {ct}");
            Assert.True(ClientTypeToolFilter.IsAllowed("Tavily", "search", ct), $"Tavily should be allowed for {ct}");
            Assert.True(ClientTypeToolFilter.IsAllowed("UserSkill_foo", "bar", ct), $"UserSkill_* should be allowed for {ct}");
            Assert.True(ClientTypeToolFilter.IsAllowed("MCP_xyz", "tool", ct), $"MCP_* should be allowed for {ct}");
        }
    }

    [Fact]
    public void NullClientType_FallsBackToChromeBehavior()
    {
        // Behaves like chrome: allows most things, not CurrentDocument
        Assert.True(ClientTypeToolFilter.IsAllowed("CLI", "run_command", null));
        Assert.True(ClientTypeToolFilter.IsAllowed("Memory", "recall", null));
        Assert.False(ClientTypeToolFilter.IsAllowed("CurrentDocument", "current_word_insert_text", null));
    }
}

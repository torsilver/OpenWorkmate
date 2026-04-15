using OfficeCopilot.Server.Services;
using OfficeCopilot.Server.Services.DynamicTooling;
using Xunit;

namespace backend.Tests.Unit;

public class ClientTypeToolFilterTests
{
    [Fact]
    public void Chrome_AllowsCli_ButNotCurrentDocument()
    {
        Assert.True(ClientTypeToolFilter.IsAllowed("CLI", "run_command", "chrome"));
        Assert.False(ClientTypeToolFilter.IsAllowed("CurrentDocument", "current_word_insert_text", "chrome"));
        Assert.False(ClientTypeToolFilter.IsAllowed("CurrentDocument", "current_ppt_document_create", "chrome"));
        Assert.True(ClientTypeToolFilter.IsAllowed("AgentTooling", DynamicToolingConstants.SearchFunctionName, "chrome"));
        Assert.True(ClientTypeToolFilter.IsAllowed("AgentTooling", DynamicToolingConstants.ActivateFunctionName, "chrome"));
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
        Assert.True(ClientTypeToolFilter.IsAllowed("CurrentDocument", "current_ppt_document_create", "wps"));
        Assert.True(ClientTypeToolFilter.IsAllowed("CurrentDocument", "current_ppt_notes_read", "wps"));
        Assert.True(ClientTypeToolFilter.IsAllowed("CurrentDocument", "current_ppt_slide_duplicate", "wps"));
        Assert.True(ClientTypeToolFilter.IsAllowed("CurrentDocument", "current_run_document_script", "wps"));
    }

    [Fact]
    public void Wps_WithHostEt_NarrowsToExcelCurrentDocument()
    {
        Assert.False(ClientTypeToolFilter.IsAllowed("CurrentDocument", "current_word_insert_text", "wps", sessionId: null, wpsHostKind: "et"));
        Assert.True(ClientTypeToolFilter.IsAllowed("CurrentDocument", "current_excel_read_range", "wps", sessionId: null, wpsHostKind: "et"));
        Assert.False(ClientTypeToolFilter.IsAllowed("CurrentDocument", "current_ppt_slides_list", "wps", sessionId: null, wpsHostKind: "et"));
        Assert.True(ClientTypeToolFilter.IsAllowed("CurrentDocument", "current_run_document_script", "wps", sessionId: null, wpsHostKind: "et"));
    }

    [Fact]
    public void Wps_WithHostWord_NarrowsToWordCurrentDocument()
    {
        Assert.True(ClientTypeToolFilter.IsAllowed("CurrentDocument", "current_word_insert_text", "wps", sessionId: null, wpsHostKind: "word"));
        Assert.False(ClientTypeToolFilter.IsAllowed("CurrentDocument", "current_excel_read_range", "wps", sessionId: null, wpsHostKind: "word"));
        Assert.True(ClientTypeToolFilter.IsAllowed("CurrentDocument", "current_run_custom_document_script", "wps", sessionId: null, wpsHostKind: "word"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("unknown")]
    [InlineData("none")]
    public void Wps_WithUnknownOrNoneHost_DoesNotNarrow(string? host)
    {
        Assert.True(ClientTypeToolFilter.IsAllowed("CurrentDocument", "current_word_insert_text", "wps", null, host));
        Assert.True(ClientTypeToolFilter.IsAllowed("CurrentDocument", "current_excel_read_range", "wps", null, host));
    }

    [Fact]
    public void Wps_WithHostWpp_NarrowsToPptCurrentDocument()
    {
        Assert.False(ClientTypeToolFilter.IsAllowed("CurrentDocument", "current_word_insert_text", "wps", null, "wpp"));
        Assert.True(ClientTypeToolFilter.IsAllowed("CurrentDocument", "current_ppt_slides_list", "wps", null, "wpp"));
    }

    [Fact]
    public void Filter_WpsEt_ExcludesWordPairs()
    {
        var pairs = new List<(string PluginName, string FunctionName)>
        {
            ("CurrentDocument", "current_word_insert_text"),
            ("CurrentDocument", "current_excel_list_sheets")
        };
        var filtered = ClientTypeToolFilter.Filter(pairs, "wps", null, "et");
        Assert.Single(filtered);
        Assert.Equal("current_excel_list_sheets", filtered[0].FunctionName);
    }

    [Fact]
    public void OfficePowerPoint_AllowsExtendedPptCurrentDocument()
    {
        Assert.True(ClientTypeToolFilter.IsAllowed("CurrentDocument", "current_ppt_hyperlink_add", "office-powerpoint"));
        Assert.True(ClientTypeToolFilter.IsAllowed("CurrentDocument", "current_ppt_table_create", "office-powerpoint"));
        Assert.True(ClientTypeToolFilter.IsAllowed("CurrentDocument", "current_ppt_document_create", "office-powerpoint"));
        Assert.False(ClientTypeToolFilter.IsAllowed("CurrentDocument", "current_ppt_notes_read", "office-word"));
    }

    [Fact]
    public void OfficeWord_DoesNotAllowCurrentPptDocumentCreate()
    {
        Assert.False(ClientTypeToolFilter.IsAllowed("CurrentDocument", "current_ppt_document_create", "office-word"));
    }

    [Fact]
    public void WpsAndOffice_AllowPdfPlugin()
    {
        Assert.True(ClientTypeToolFilter.IsAllowed("Pdf", "get_pdf_text", "wps"));
        Assert.True(ClientTypeToolFilter.IsAllowed("Pdf", "get_pdf_info", "office-word"));
        Assert.True(ClientTypeToolFilter.IsAllowed("Pdf", "pdf_merge", "office-excel"));
        Assert.True(ClientTypeToolFilter.IsAllowed("Pdf", "pdf_document_create", "office-powerpoint"));
        Assert.True(ClientTypeToolFilter.IsAllowed("Pdf", "get_pdf_text", "chrome"));
    }

    [Fact]
    public void CommonPlugins_AllowedOnAllClientTypes()
    {
        var clientTypes = new[] { "chrome", "office-word", "office-excel", "office-powerpoint", "wps" };
        foreach (var ct in clientTypes)
        {
            Assert.True(ClientTypeToolFilter.IsAllowed("Memory", "recall", ct), $"Memory should be allowed for {ct}");
            Assert.True(ClientTypeToolFilter.IsAllowed("ClawhubSkill", "run_clawhub_script", ct), $"ClawhubSkill should be allowed for {ct}");
            Assert.True(ClientTypeToolFilter.IsAllowed("UserSkill_foo", "bar", ct), $"UserSkill_* should be allowed for {ct}");
            Assert.True(
                ClientTypeToolFilter.IsAllowed("UserSkillProgressive", DynamicToolingConstants.SearchAvailableSkillsFunctionName, ct),
                $"UserSkillProgressive search should be allowed for {ct}");
            Assert.True(
                ClientTypeToolFilter.IsAllowed("UserSkillProgressive", DynamicToolingConstants.SelectSkillForTurnFunctionName, ct),
                $"UserSkillProgressive select should be allowed for {ct}");
            Assert.True(
                ClientTypeToolFilter.IsAllowed("UserSkillProgressive", "load_user_skill_instructions", ct),
                $"UserSkillProgressive should be allowed for {ct}");
            Assert.True(ClientTypeToolFilter.IsAllowed("MCP_xyz", "tool", ct), $"MCP_* should be allowed for {ct}");
            Assert.True(
                ClientTypeToolFilter.IsAllowed("AgentTooling", DynamicToolingConstants.SearchFunctionName, ct),
                $"AgentTooling search should be allowed for {ct}");
            Assert.True(
                ClientTypeToolFilter.IsAllowed("AgentTooling", DynamicToolingConstants.ActivateFunctionName, ct),
                $"AgentTooling activate should be allowed for {ct}");
            Assert.True(ClientTypeToolFilter.IsAllowed("CLI", "run_command", ct), $"CLI run_command should be allowed for {ct}");
            Assert.True(ClientTypeToolFilter.IsAllowed("File", "text_file_read", ct), $"File should be allowed for {ct}");
            Assert.True(ClientTypeToolFilter.IsAllowed("System", "get_current_time", ct), $"System should be allowed for {ct}");
            Assert.True(ClientTypeToolFilter.IsAllowed("UserOptions", "ask_options", ct), $"UserOptions should be allowed for {ct}");
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

    [Fact]
    public void ScheduledRunnerSession_BlocksScheduledTaskMutations()
    {
        const string sid = "scheduled:task-abc";
        Assert.False(ClientTypeToolFilter.IsAllowed("ScheduledTask", "scheduled_task_create", "chrome", sid));
        Assert.False(ClientTypeToolFilter.IsAllowed("ScheduledTask", "scheduled_task_update", "office-word", sid));
        Assert.False(ClientTypeToolFilter.IsAllowed("ScheduledTask", "scheduled_task_delete", "wps", sid));
    }

    [Fact]
    public void ScheduledRunnerSession_AllowsScheduledTaskReadAndList()
    {
        const string sid = "scheduled:task-abc";
        Assert.True(ClientTypeToolFilter.IsAllowed("ScheduledTask", "scheduled_task_list", "chrome", sid));
        Assert.True(ClientTypeToolFilter.IsAllowed("ScheduledTask", "scheduled_task_read", "chrome", sid));
    }

    [Fact]
    public void NonScheduledSession_AllowsScheduledTaskCreate()
    {
        Assert.True(ClientTypeToolFilter.IsAllowed("ScheduledTask", "scheduled_task_create", "chrome", null));
        Assert.True(ClientTypeToolFilter.IsAllowed("ScheduledTask", "scheduled_task_create", "chrome", "user-session-1"));
    }

    [Fact]
    public void Subagent_RunBrowserSubtask_OnlyChrome()
    {
        Assert.True(ClientTypeToolFilter.IsAllowed("Subagent", "run_browser_subtask", "chrome"));
        Assert.True(ClientTypeToolFilter.IsAllowed("Subagent", "run_browser_subtask", null));
        Assert.False(ClientTypeToolFilter.IsAllowed("Subagent", "run_browser_subtask", "office-word"));
        Assert.False(ClientTypeToolFilter.IsAllowed("Subagent", "run_browser_subtask", "wps"));
    }

    [Fact]
    public void Subagent_RunCliSubtask_FollowsCliRunCommandVisibility()
    {
        Assert.True(ClientTypeToolFilter.IsAllowed("Subagent", "run_cli_subtask", "chrome"));
        Assert.True(ClientTypeToolFilter.IsAllowed("Subagent", "run_cli_subtask", "office-word"));
        Assert.True(ClientTypeToolFilter.IsAllowed("Subagent", "run_cli_subtask", "office-excel"));
        Assert.True(ClientTypeToolFilter.IsAllowed("Subagent", "run_cli_subtask", "office-powerpoint"));
        Assert.True(ClientTypeToolFilter.IsAllowed("Subagent", "run_cli_subtask", "wps"));
    }

    [Fact]
    public void Subagent_RunExploreAndRunSubtask_AllowedOnOfficeWord()
    {
        Assert.True(ClientTypeToolFilter.IsAllowed("Subagent", "run_subtask", "office-word"));
        Assert.True(ClientTypeToolFilter.IsAllowed("Subagent", "run_explore_subtask", "office-word"));
        Assert.False(ClientTypeToolFilter.IsAllowed("Subagent", "run_browser_subtask", "office-word"));
    }
}

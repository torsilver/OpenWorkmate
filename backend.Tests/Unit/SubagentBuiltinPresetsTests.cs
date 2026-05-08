using Microsoft.Extensions.AI;
using OpenWorkmate.Server.Services;
using OpenWorkmate.Server.Services.Subagent;
using Xunit;

namespace backend.Tests.Unit;

public class SubagentBuiltinPresetsTests
{
    private static AITool DummyTool(string plugin, string func)
    {
        return AIFunctionFactory.Create(
            () => Task.FromResult(""),
            new AIFunctionFactoryOptions { Name = func, Description = "d" });
    }

    [Fact]
    public void FilterToolsForSubtask_General_StripsSubagentEntriesAndCompact()
    {
        var reg = new ToolRegistry();
        reg.Register("File", "text_file_read", DummyTool("File", "text_file_read"));
        reg.Register("Subagent", "run_subtask", DummyTool("Subagent", "run_subtask"));
        reg.Register("Context", "compact_conversation", DummyTool("Context", "compact_conversation"));

        var session = new List<AITool>
        {
            reg.FindTool("File", "text_file_read")!,
            reg.FindTool("Subagent", "run_subtask")!,
            reg.FindTool("Context", "compact_conversation")!,
        };

        var list = SubagentBuiltinPresets.FilterToolsForSubtask(reg, session, SubagentBuiltinPreset.General, out var err);
        Assert.Null(err);
        Assert.Single(list);
        Assert.Equal("text_file_read", list[0].Name);
    }

    [Fact]
    public void FilterToolsForSubtask_Explore_KeepsOnlyWhitelistedPlugins()
    {
        var reg = new ToolRegistry();
        reg.Register("File", "text_file_read", DummyTool("File", "text_file_read"));
        reg.Register("CLI", "run_command", DummyTool("CLI", "run_command"));
        reg.Register("Memory", "recall", DummyTool("Memory", "recall"));

        var session = new List<AITool>
        {
            reg.FindTool("File", "text_file_read")!,
            reg.FindTool("CLI", "run_command")!,
            reg.FindTool("Memory", "recall")!,
        };

        var list = SubagentBuiltinPresets.FilterToolsForSubtask(reg, session, SubagentBuiltinPreset.Explore, out var err);
        Assert.Null(err);
        var names = list.Select(t => t.Name).OrderBy(x => x, StringComparer.Ordinal).ToArray();
        Assert.Equal(new[] { "recall", "text_file_read" }, names);
    }

    [Fact]
    public void FilterToolsForSubtask_CliShell_RequiresCliTools()
    {
        var reg = new ToolRegistry();
        reg.Register("File", "text_file_read", DummyTool("File", "text_file_read"));

        var session = new List<AITool> { reg.FindTool("File", "text_file_read")! };
        var list = SubagentBuiltinPresets.FilterToolsForSubtask(reg, session, SubagentBuiltinPreset.CliShell, out var err);
        Assert.NotNull(err);
        Assert.Contains("终端", err, StringComparison.Ordinal);
        Assert.Empty(list);
    }

    [Fact]
    public void FilterToolsForSubtask_Browser_RequiresBrowserTools()
    {
        var reg = new ToolRegistry();
        reg.Register("File", "text_file_read", DummyTool("File", "text_file_read"));

        var session = new List<AITool> { reg.FindTool("File", "text_file_read")! };
        var list = SubagentBuiltinPresets.FilterToolsForSubtask(reg, session, SubagentBuiltinPreset.Browser, out var err);
        Assert.NotNull(err);
        Assert.Contains("浏览器", err, StringComparison.Ordinal);
        Assert.Empty(list);
    }
}

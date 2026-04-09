using OfficeCopilot.Server.Services;
using Xunit;

namespace backend.Tests.Unit;

public class ToolCapabilityRegistryTests
{
    [Fact]
    public void Get_ExactOverride_CLI()
    {
        var c = ToolCapabilityRegistry.Get("CLI", "run_command");
        Assert.False(c.ReadOnly);
        Assert.True(c.Destructive);
        Assert.True(c.SuggestHitl);
    }

    [Fact]
    public void Get_Heuristic_ReadSuffix()
    {
        var c = ToolCapabilityRegistry.Get("Excel", "excel_range_read");
        Assert.True(c.ReadOnly);
        Assert.True(c.AllowParallelSameTurn);
    }

    [Fact]
    public void Get_Heuristic_Write()
    {
        var c = ToolCapabilityRegistry.Get("Excel", "excel_range_write");
        Assert.True(c.Destructive);
        Assert.False(c.AllowParallelSameTurn);
    }

    [Fact]
    public void Get_ExactOverride_PdfMerge()
    {
        var c = ToolCapabilityRegistry.Get("Pdf", "pdf_merge");
        Assert.False(c.ReadOnly);
        Assert.True(c.Destructive);
        Assert.False(c.AllowParallelSameTurn);
    }

    [Fact]
    public void Get_Heuristic_FileTextFileRead()
    {
        var c = ToolCapabilityRegistry.Get("File", "text_file_read");
        Assert.True(c.ReadOnly);
        Assert.True(c.AllowParallelSameTurn);
    }

    [Fact]
    public void Get_Heuristic_FileTextFileWrite()
    {
        var c = ToolCapabilityRegistry.Get("File", "text_file_write");
        Assert.True(c.Destructive);
        Assert.False(c.AllowParallelSameTurn);
    }
}

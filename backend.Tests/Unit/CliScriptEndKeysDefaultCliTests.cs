using OfficeCopilot.Server;
using Xunit;

namespace backend.Tests.Unit;

public class CliScriptEndKeysDefaultCliTests
{
    [Fact]
    public void GetDefaultAllowedCliCommands_Backend_IncludesDir()
    {
        var list = CliScriptEndKeys.GetDefaultAllowedCliCommands(CliScriptEndKeys.Backend);
        Assert.Contains("dir", list);
        Assert.Equal(CliScriptEndKeys.DefaultAllowedCommands.Length, list.Count);
    }

    [Fact]
    public void GetDefaultAllowedCliCommands_ChromeOfficeWps_MatchBackendDefaults()
    {
        var backend = CliScriptEndKeys.GetDefaultAllowedCliCommands(CliScriptEndKeys.Backend);
        Assert.Equal(backend, CliScriptEndKeys.GetDefaultAllowedCliCommands(CliScriptEndKeys.Chrome));
        Assert.Equal(backend, CliScriptEndKeys.GetDefaultAllowedCliCommands(CliScriptEndKeys.Office));
        Assert.Equal(backend, CliScriptEndKeys.GetDefaultAllowedCliCommands(CliScriptEndKeys.Wps));
    }

    [Fact]
    public void GetDefaultAllowedCliCommands_NullOrUnknown_FallsBackToBackendDefaults()
    {
        var listNull = CliScriptEndKeys.GetDefaultAllowedCliCommands(null);
        var listEmpty = CliScriptEndKeys.GetDefaultAllowedCliCommands("   ");
        Assert.Contains("dir", listNull);
        Assert.Contains("dir", listEmpty);
    }

    [Fact]
    public void GetDefaultAllowedDocumentScriptIds_Office_MatchesTaskpaneRegistry()
    {
        var list = CliScriptEndKeys.GetDefaultAllowedDocumentScriptIds(CliScriptEndKeys.Office);
        Assert.Contains("word_read_selection", list);
        Assert.Contains("office_doc_meta", list);
        Assert.Equal(CliScriptEndKeys.DefaultAllowedDocumentScriptIdsOffice.Length, list.Count);
    }

    [Fact]
    public void GetDefaultAllowedDocumentScriptIds_Wps_MatchesTaskpaneRegistry()
    {
        var list = CliScriptEndKeys.GetDefaultAllowedDocumentScriptIds(CliScriptEndKeys.Wps);
        Assert.Contains("wps_doc_meta", list);
        Assert.Equal(CliScriptEndKeys.DefaultAllowedDocumentScriptIdsWps.Length, list.Count);
    }

    [Fact]
    public void GetDefaultAllowedDocumentScriptIds_Backend_Empty()
    {
        Assert.Empty(CliScriptEndKeys.GetDefaultAllowedDocumentScriptIds(CliScriptEndKeys.Backend));
    }
}

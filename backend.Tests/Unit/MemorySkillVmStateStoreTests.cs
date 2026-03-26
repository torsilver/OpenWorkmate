using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using OfficeCopilot.Server;
using OfficeCopilot.Server.Services.SkillVm;
using Xunit;

namespace backend.Tests.Unit;

public class MemorySkillVmStateStoreTests
{
    [Fact]
    public void GetOrCreate_initializes_and_updates_same_session()
    {
        var cfg = CreateConfigService();
        var store = new MemorySkillVmStateStore(cfg, NullLogger<MemorySkillVmStateStore>.Instance);
        var s = store.GetOrCreate("s1", "skillA", "seg1");
        Assert.Equal("seg1", s.CurrentSegmentId);
        s.CurrentSegmentId = "seg2";
        store.Update("s1", s);
        Assert.True(store.TryGet("s1", out var s2));
        Assert.Equal("seg2", s2!.CurrentSegmentId);
    }

    [Fact]
    public void Clear_removes_session()
    {
        var cfg = CreateConfigService();
        var store = new MemorySkillVmStateStore(cfg, NullLogger<MemorySkillVmStateStore>.Instance);
        store.GetOrCreate("x", "a", "1");
        store.Clear("x");
        Assert.False(store.TryGet("x", out _));
    }

    private static ConfigService CreateConfigService()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>()).Build();
        return new ConfigService(configuration, NullLogger<ConfigService>.Instance);
    }
}

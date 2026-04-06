using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using OfficeCopilot.Server.Services;
using OfficeCopilot.Server.Services.Memory;
using Xunit;

namespace backend.Tests.Unit;

public sealed class ToolIndexServiceSyncTests
{
    private sealed class CountingEmbedding : IEmbeddingProvider
    {
        public int Calls { get; private set; }
        public bool IsConfigured => true;
        public Task<float[]?> GenerateEmbeddingAsync(string text, CancellationToken ct = default)
        {
            Calls++;
            return Task.FromResult<float[]?>(new[] { Calls * 0.1f, 0f, 0f });
        }
    }

    private static ToolRegistry RegistryWithUserSkill(string pluginName, string description)
    {
        var registry = new ToolRegistry();
        var fn = AIFunctionFactory.Create(
            () => Task.FromResult("ok"),
            new AIFunctionFactoryOptions { Name = "run", Description = description });
        registry.Register(pluginName, "run", fn);
        return registry;
    }

    [Fact]
    public async Task SyncUserToolIndex_SecondRunWithSameText_DoesNotCallEmbeddingAgain()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), "toolidx-sync-" + Guid.NewGuid().ToString("N") + ".db");
        var embedding = new CountingEmbedding();
        var store = new SqliteVectorStore("Data Source=" + dbPath);
        var svc = new ToolIndexService(embedding, store, NullLogger<ToolIndexService>.Instance);
        var registry = RegistryWithUserSkill("UserSkill_Alpha", "same");

        try
        {
            await svc.SyncUserToolIndexAsync(registry);
            var afterFirst = embedding.Calls;
            Assert.True(afterFirst > 0);
            await svc.SyncUserToolIndexAsync(registry);
            Assert.Equal(afterFirst, embedding.Calls);
        }
        finally
        {
            try { File.Delete(dbPath); } catch { /* ignore */ }
        }
    }

    [Fact]
    public async Task SyncUserToolIndex_DescriptionChange_TriggersNewEmbeddings()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), "toolidx-chg-" + Guid.NewGuid().ToString("N") + ".db");
        var embedding = new CountingEmbedding();
        var store = new SqliteVectorStore("Data Source=" + dbPath);
        var svc = new ToolIndexService(embedding, store, NullLogger<ToolIndexService>.Instance);

        try
        {
            await svc.SyncUserToolIndexAsync(RegistryWithUserSkill("UserSkill_Beta", "v1"));
            var afterFirst = embedding.Calls;
            await svc.SyncUserToolIndexAsync(RegistryWithUserSkill("UserSkill_Beta", "v2"));
            Assert.True(embedding.Calls > afterFirst);
        }
        finally
        {
            try { File.Delete(dbPath); } catch { /* ignore */ }
        }
    }

    [Fact]
    public async Task SyncUserToolIndex_RemovedPlugin_DeletesOrphanUserVectors()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), "toolidx-orph-" + Guid.NewGuid().ToString("N") + ".db");
        var embedding = new CountingEmbedding();
        var store = new SqliteVectorStore("Data Source=" + dbPath);
        var svc = new ToolIndexService(embedding, store, NullLogger<ToolIndexService>.Instance);

        try
        {
            await svc.SyncUserToolIndexAsync(RegistryWithUserSkill("UserSkill_Old", "x"));
            var orphansBefore = await store.ListIdsByCollectionAndToolSourceAsync("tools:chrome", "user");
            Assert.Contains(orphansBefore, id => id.Contains("UserSkill_Old", StringComparison.Ordinal));

            await svc.SyncUserToolIndexAsync(RegistryWithUserSkill("UserSkill_New", "y"));
            var orphansAfter = await store.ListIdsByCollectionAndToolSourceAsync("tools:chrome", "user");
            Assert.DoesNotContain(orphansAfter, id => id.Contains("UserSkill_Old", StringComparison.Ordinal));
            Assert.Contains(orphansAfter, id => id.Contains("UserSkill_New", StringComparison.Ordinal));
        }
        finally
        {
            try { File.Delete(dbPath); } catch { /* ignore */ }
        }
    }
}

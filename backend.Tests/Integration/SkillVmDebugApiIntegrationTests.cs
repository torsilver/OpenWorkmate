using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OfficeCopilot.Server;
using OfficeCopilot.Server.Services.SkillVm;
using Xunit;

namespace backend.Tests.Integration;

public class SkillVmDebugApiIntegrationTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _client;

    public SkillVmDebugApiIntegrationTests(WebApplicationFactory<Program> factory)
    {
        var tempUserConfigPath = Path.Combine(
            Path.GetTempPath(),
            "OfficeCopilot.user-config-test-" + Guid.NewGuid().ToString("N") + ".json");
        var tempScheduledTasksDir = Path.Combine(
            Path.GetTempPath(),
            "OfficeCopilot.scheduled-tasks-test-" + Guid.NewGuid().ToString("N"));

        _factory = factory.WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment(Environments.Development);
            builder.ConfigureAppConfiguration((_, config) =>
            {
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["RagStorageType"] = "Memory",
                    ["PlansDirectory"] = "",
                    ["ScheduledTasksDirectory"] = tempScheduledTasksDir,
                    ["OfficeCopilot:UserConfigPath"] = tempUserConfigPath,
                    ["WebSocket:AuthToken"] = "",
                });
            });
        });
        _client = _factory.CreateClient();
    }

    [Fact]
    public async Task GetDebugSkillVm_WithState_Returns200_AndInjectionPreview()
    {
        const string sid = "sess-skill-vm-debug";
        using (var scope = _factory.Services.CreateScope())
        {
            var store = scope.ServiceProvider.GetRequiredService<ISkillVmStateStore>();
            store.GetOrCreate(sid, "skill-vm-demo", "intro");
        }

        var response = await _client.GetAsync("/api/debug/skill-vm/" + sid);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadAsStringAsync();
        var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        Assert.True(root.GetProperty("ok").GetBoolean());
        Assert.True(root.GetProperty("injectionPreview").GetString()?.Contains("intro", StringComparison.OrdinalIgnoreCase) == true);
        Assert.True(root.GetProperty("estimatedTokens").GetInt32() > 0);
    }

    [Fact]
    public async Task GetDebugSkillVm_NoState_Returns404_WithMessage()
    {
        var response = await _client.GetAsync("/api/debug/skill-vm/nonexistent-session-xyz");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var json = await response.Content.ReadAsStringAsync();
        var doc = JsonDocument.Parse(json);
        Assert.False(doc.RootElement.GetProperty("ok").GetBoolean());
        Assert.Contains("未找到", doc.RootElement.GetProperty("message").GetString() ?? "");
    }

    [Fact]
    public async Task PostDebugSkillVmFlags_SetsPauseFlags()
    {
        const string sid = "sess-flags";
        var body = JsonSerializer.Serialize(new { pauseBeforeInject = true, pauseAfterSkillStep = false });
        var content = new StringContent(body, Encoding.UTF8, "application/json");
        var response = await _client.PostAsync("/api/debug/skill-vm/" + sid + "/flags", content);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadAsStringAsync();
        var doc = JsonDocument.Parse(json);
        Assert.True(doc.RootElement.GetProperty("ok").GetBoolean());
        var flags = doc.RootElement.GetProperty("flags");
        Assert.True(flags.GetProperty("pauseBeforeInject").GetBoolean());
        Assert.False(flags.GetProperty("pauseAfterSkillStep").GetBoolean());
    }

    [Fact]
    public async Task PostDebugSkillVmFlags_InvalidJson_Returns400_WithMessage()
    {
        var content = new StringContent("{not-json", Encoding.UTF8, "application/json");
        var response = await _client.PostAsync("/api/debug/skill-vm/x/flags", content);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var json = await response.Content.ReadAsStringAsync();
        Assert.Contains("解析", JsonDocument.Parse(json).RootElement.GetProperty("message").GetString() ?? "");
    }
}

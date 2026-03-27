using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.IO;
using System.Collections.Generic;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Configuration;
using OfficeCopilot.Server;
using Xunit;

namespace backend.Tests.Integration;

/// <summary>与 ApiIntegrationTests 相同：隔离用户配置与定时任务目录；using 结束时删除临时文件，避免 Temp 下堆积。</summary>
internal sealed class IsolatedAuthWebAppFactory : IDisposable
{
    public WebApplicationFactory<Program> Factory { get; }

    private readonly string _userConfigPath;
    private readonly string _scheduledTasksDir;

    private IsolatedAuthWebAppFactory(
        WebApplicationFactory<Program> factory,
        string userConfigPath,
        string scheduledTasksDir)
    {
        Factory = factory;
        _userConfigPath = userConfigPath;
        _scheduledTasksDir = scheduledTasksDir;
    }

    public static IsolatedAuthWebAppFactory Create(string? authToken)
    {
        var userConfigPath = Path.Combine(
            Path.GetTempPath(),
            "OfficeCopilot.user-config-test-" + Guid.NewGuid().ToString("N") + ".json");
        var scheduledTasksDir = Path.Combine(
            Path.GetTempPath(),
            "OfficeCopilot.scheduled-tasks-test-" + Guid.NewGuid().ToString("N"));

        var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment(Environments.Development);
            builder.ConfigureAppConfiguration((_, config) =>
            {
                var dict = new Dictionary<string, string?>
                {
                    ["RagStorageType"] = "Memory",
                    ["PlansDirectory"] = "",
                    ["ScheduledTasksDirectory"] = scheduledTasksDir,
                    ["OfficeCopilot:UserConfigPath"] = userConfigPath,
                    ["WebSocket:AuthToken"] = string.IsNullOrEmpty(authToken) ? "" : authToken,
                };
                config.AddInMemoryCollection(dict);
            });
        });
        return new IsolatedAuthWebAppFactory(factory, userConfigPath, scheduledTasksDir);
    }

    public void Dispose()
    {
        Factory.Dispose();
        try
        {
            if (File.Exists(_userConfigPath))
                File.Delete(_userConfigPath);
        }
        catch
        {
            /* ignore */
        }

        try
        {
            if (Directory.Exists(_scheduledTasksDir))
                Directory.Delete(_scheduledTasksDir, true);
        }
        catch
        {
            /* ignore */
        }
    }
}

public class SecurityAuthIntegrationTests
{

    [Fact]
    public async Task GetConfig_WhenAuthTokenConfigured_WithoutHeader_Returns401()
    {
        using var fac = IsolatedAuthWebAppFactory.Create("integration-secret-token");
        var client = fac.Factory.CreateClient();
        var res = await client.GetAsync("/api/config");
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
        var json = await res.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        Assert.False(doc.RootElement.GetProperty("ok").GetBoolean());
        Assert.Contains("未授权", doc.RootElement.GetProperty("message").GetString() ?? "");
    }

    [Fact]
    public async Task GetConfig_WhenAuthTokenConfigured_WithXHeader_Returns200()
    {
        using var fac = IsolatedAuthWebAppFactory.Create("integration-secret-token");
        var client = fac.Factory.CreateClient();
        var req = new HttpRequestMessage(HttpMethod.Get, "/api/config");
        req.Headers.TryAddWithoutValidation("X-OfficeCopilot-Token", "integration-secret-token");
        var res = await client.SendAsync(req);
        res.EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task GetConfig_WhenAuthTokenConfigured_WithBearer_Returns200()
    {
        using var fac = IsolatedAuthWebAppFactory.Create("integration-secret-token");
        var client = fac.Factory.CreateClient();
        var req = new HttpRequestMessage(HttpMethod.Get, "/api/config");
        req.Headers.TryAddWithoutValidation("Authorization", "Bearer integration-secret-token");
        var res = await client.SendAsync(req);
        res.EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task PostTestAi_LocalhostEndpoint_Returns400_WithMessage()
    {
        using var fac = IsolatedAuthWebAppFactory.Create(null);
        var client = fac.Factory.CreateClient();
        var body = new { endpoint = "http://127.0.0.1:9999/v1", modelId = "m", apiKey = "k" };
        var content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        var res = await client.PostAsync("/api/config/test-ai", content);
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        var json = await res.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        Assert.False(doc.RootElement.GetProperty("ok").GetBoolean());
        Assert.False(string.IsNullOrWhiteSpace(doc.RootElement.GetProperty("message").GetString()));
    }

    [Fact]
    public async Task BootstrapLocalServiceAuth_WithoutHeader_Returns200_AndEffectiveToken()
    {
        const string secret = "bootstrap-test-token-xyz";
        using var fac = IsolatedAuthWebAppFactory.Create(secret);
        var client = fac.Factory.CreateClient();
        var res = await client.GetAsync("/api/bootstrap/local-service-auth");
        res.EnsureSuccessStatusCode();
        var json = await res.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        Assert.True(doc.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal(secret, doc.RootElement.GetProperty("webSocketAuthToken").GetString());
        Assert.True(doc.RootElement.TryGetProperty("localServiceBaseUrl", out var baseUrlEl));
        Assert.False(string.IsNullOrWhiteSpace(baseUrlEl.GetString()));
        Assert.True(doc.RootElement.TryGetProperty("localServicePortScanStart", out var scanStart));
        Assert.True(scanStart.TryGetInt32(out var start) && start >= 1);
        Assert.True(doc.RootElement.TryGetProperty("localServicePortScanCount", out var scanCount));
        Assert.True(scanCount.TryGetInt32(out var cnt) && cnt >= 1);
    }

    [Fact]
    public async Task BootstrapLocalServiceAuth_WhenNoServerToken_Returns200_WithNullOrEmptyToken()
    {
        using var fac = IsolatedAuthWebAppFactory.Create(null);
        var client = fac.Factory.CreateClient();
        var res = await client.GetAsync("/api/bootstrap/local-service-auth");
        res.EnsureSuccessStatusCode();
        var json = await res.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        Assert.True(doc.RootElement.GetProperty("ok").GetBoolean());
        if (doc.RootElement.TryGetProperty("webSocketAuthToken", out var tok))
        {
            var s = tok.GetString();
            Assert.True(string.IsNullOrEmpty(s));
        }
    }
}

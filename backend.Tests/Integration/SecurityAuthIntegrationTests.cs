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
using Microsoft.Extensions.DependencyInjection;
using OfficeCopilot.Server;
using Xunit;

namespace backend.Tests.Integration;

public class SecurityAuthIntegrationTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public SecurityAuthIntegrationTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory;
    }

    private static WebApplicationFactory<Program> CreateFactory(string? authToken)
    {
        var tempUserConfigPath = Path.Combine(
            Path.GetTempPath(),
            "OfficeCopilot.user-config-test-" + Guid.NewGuid().ToString("N") + ".json");

        return new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment(Environments.Development);
            builder.ConfigureAppConfiguration((_, config) =>
            {
                var dict = new Dictionary<string, string?>
                {
                    ["RagStorageType"] = "Memory",
                    ["PlansDirectory"] = "",
                    ["ScheduledTasksDirectory"] = "",
                    ["OfficeCopilot:UserConfigPath"] = tempUserConfigPath,
                    ["WebSocket:AuthToken"] = string.IsNullOrEmpty(authToken) ? "" : authToken,
                };
                config.AddInMemoryCollection(dict);
            });
            builder.ConfigureServices(services =>
            {
                services.AddHttpClient("STT")
                    .ConfigurePrimaryHttpMessageHandler(() => new SttFakeHttpMessageHandler());
            });
        });
    }

    [Fact]
    public async Task GetConfig_WhenAuthTokenConfigured_WithoutHeader_Returns401()
    {
        using var fac = CreateFactory("integration-secret-token");
        var client = fac.CreateClient();
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
        using var fac = CreateFactory("integration-secret-token");
        var client = fac.CreateClient();
        var req = new HttpRequestMessage(HttpMethod.Get, "/api/config");
        req.Headers.TryAddWithoutValidation("X-OfficeCopilot-Token", "integration-secret-token");
        var res = await client.SendAsync(req);
        res.EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task GetConfig_WhenAuthTokenConfigured_WithBearer_Returns200()
    {
        using var fac = CreateFactory("integration-secret-token");
        var client = fac.CreateClient();
        var req = new HttpRequestMessage(HttpMethod.Get, "/api/config");
        req.Headers.TryAddWithoutValidation("Authorization", "Bearer integration-secret-token");
        var res = await client.SendAsync(req);
        res.EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task PostTestAi_LocalhostEndpoint_Returns400_WithMessage()
    {
        using var fac = CreateFactory(null);
        var client = fac.CreateClient();
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
        using var fac = CreateFactory(secret);
        var client = fac.CreateClient();
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
        using var fac = CreateFactory(null);
        var client = fac.CreateClient();
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

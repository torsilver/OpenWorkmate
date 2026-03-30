using System.Net;
using System.Net.Http;
using System.Net.WebSockets;
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

        IntegrationTestUserConfigWriter.Write(userConfigPath, scheduledTasksDir, authToken);

        var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment(Environments.Development);
            builder.ConfigureAppConfiguration((_, config) =>
            {
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["OfficeCopilot:UserConfigPath"] = userConfigPath,
                });
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

    /// <summary>
    /// 扩展侧栏 WebSocket 无法在握手时携带 X-OfficeCopilot-Token；须仅用查询参数 token 通过 LocalApiAuthMiddleware 到达 /api/stt-stream。
    /// </summary>
    [Fact]
    public async Task SttStream_WebSocket_WhenAuthConfigured_QueryTokenWithoutHeader_OpensSocket_AndServerSendsConfigError()
    {
        const string secret = "integration-secret-token";
        using var fac = IsolatedAuthWebAppFactory.Create(secret);
        var server = fac.Factory.Server;
        var wsClient = server.CreateWebSocketClient();
        var baseUri = server.BaseAddress ?? new Uri("http://localhost");
        var wsUri = new UriBuilder(baseUri)
        {
            Scheme = string.Equals(baseUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ? "wss" : "ws",
            Path = "/api/stt-stream",
            Query = "token=" + Uri.EscapeDataString(secret) + "&mode=inline"
        }.Uri;

        using var socket = await wsClient.ConnectAsync(wsUri, CancellationToken.None);
        var buf = new byte[8192];
        var r = await socket.ReceiveAsync(new ArraySegment<byte>(buf), CancellationToken.None);
        Assert.Equal(WebSocketMessageType.Text, r.MessageType);
        var text = Encoding.UTF8.GetString(buf, 0, r.Count);
        using var msg = JsonDocument.Parse(text);
        Assert.Equal("error", msg.RootElement.GetProperty("type").GetString());
        var m = msg.RootElement.GetProperty("message").GetString() ?? "";
        Assert.Contains("百炼", m);
        if (socket.State == WebSocketState.Open)
        {
            try
            {
                await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None);
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
                // 服务端可能已在下发错误后主动关闭，TestHost 上再 CloseAsync 会抛错，忽略即可
            }
        }
    }
}

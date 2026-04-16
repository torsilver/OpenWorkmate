using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Taskly.Telemetry.Relay.Tests;

public sealed class AdminGenerateApiKeyIntegrationTests : IClassFixture<RelayWebApplicationFactory>, IDisposable
{
    private readonly RelayWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public AdminGenerateApiKeyIntegrationTests(RelayWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
        _client.DefaultRequestHeaders.TryAddWithoutValidation("X-Admin-Key", "test-admin-key");
    }

    public void Dispose() => _client.Dispose();

    [Fact]
    public async Task Generate_api_key_writes_appsettings_and_returns_key()
    {
        var res = await _client.PostAsync("/admin/telemetry/generate-api-key", new StringContent("", Encoding.UTF8, "application/json"));
        res.EnsureSuccessStatusCode();
        var json = await res.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        Assert.True(doc.RootElement.GetProperty("ok").GetBoolean());
        var apiKey = doc.RootElement.GetProperty("apiKey").GetString();
        Assert.False(string.IsNullOrWhiteSpace(apiKey));

        var path = Path.Combine(_factory.ContentRoot, "appsettings.json");
        Assert.True(File.Exists(path));
        var fileText = await File.ReadAllTextAsync(path);
        using var fileDoc = JsonDocument.Parse(fileText);
        var fileKey = fileDoc.RootElement.GetProperty("Telemetry").GetProperty("ApiKey").GetString();
        Assert.Equal(apiKey, fileKey);
    }

    [Fact]
    public async Task Ingest_accepts_key_after_generate_and_reload()
    {
        var res = await _client.PostAsync("/admin/telemetry/generate-api-key", new StringContent("", Encoding.UTF8, "application/json"));
        res.EnsureSuccessStatusCode();
        var json = await res.Content.ReadAsStringAsync();
        var apiKey = JsonDocument.Parse(json).RootElement.GetProperty("apiKey").GetString()!;

        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        var deviceId = Guid.NewGuid().ToString();
        var body = new
        {
            deviceId,
            clientTier = "full",
            events = new[] { new { sessionId = "s1", eventType = "tool_invocation", detailLevel = "p0", message = "x" } }
        };
        var ingestRes = await _client.PostAsync("/ingest/batch",
            new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"));
        ingestRes.EnsureSuccessStatusCode();
    }
}

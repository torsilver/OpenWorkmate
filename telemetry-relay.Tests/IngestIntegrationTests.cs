using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Taskly.Telemetry.Relay.Tests;

public sealed class IngestIntegrationTests : IClassFixture<RelayWebApplicationFactory>, IDisposable
{
    private readonly RelayWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public IngestIntegrationTests(RelayWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    public void Dispose() => _client.Dispose();

    [Fact]
    public async Task Ingest_without_key_returns_401()
    {
        var res = await _client.PostAsync("/ingest/batch", new StringContent("{}", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    [Fact]
    public async Task Ingest_writes_session_file()
    {
        var deviceId = Guid.NewGuid().ToString();
        var sessionId = "abcd1234";
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "test-ingest-key");
        var body = new
        {
            deviceId,
            clientTier = "full",
            events = new[]
            {
                new
                {
                    sessionId,
                    eventType = "tool_invocation",
                    detailLevel = "p0",
                    message = "ok"
                }
            }
        };
        var json = JsonSerializer.Serialize(body);
        var res = await _client.PostAsync("/ingest/batch", new StringContent(json, Encoding.UTF8, "application/json"));
        res.EnsureSuccessStatusCode();
        var path = Path.Combine(_factory.DataRoot, "devices", deviceId, "sessions", sessionId + ".txt");
        Assert.True(File.Exists(path), "session file should exist: " + path);
        var text = await File.ReadAllTextAsync(path);
        Assert.Contains("tool_invocation", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Ingest_invalid_deviceId_returns_400()
    {
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "test-ingest-key");
        var body = new
        {
            deviceId = "not-a-guid",
            clientTier = "full",
            events = new[] { new { sessionId = "x", eventType = "e", detailLevel = "p0" } }
        };
        var res = await _client.PostAsync("/ingest/batch",
            new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task Ingest_invalid_sessionId_skipped()
    {
        var deviceId = Guid.NewGuid().ToString();
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "test-ingest-key");
        var body = new
        {
            deviceId,
            clientTier = "full",
            events = new[] { new { sessionId = "../evil", eventType = "e", detailLevel = "p0" } }
        };
        var res = await _client.PostAsync("/ingest/batch",
            new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"));
        res.EnsureSuccessStatusCode();
        var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        Assert.Equal(0, doc.RootElement.GetProperty("accepted").GetInt32());
        Assert.Equal(1, doc.RootElement.GetProperty("skipped").GetInt32());
    }

    [Fact]
    public async Task Ingest_assistant_turn_final_with_payload_minimal_line_contains_preview_and_charCount()
    {
        var deviceId = Guid.NewGuid().ToString();
        var sessionId = "sess001";
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "test-ingest-key");
        var body = new
        {
            deviceId,
            clientTier = "minimal",
            events = new[]
            {
                new
                {
                    sessionId,
                    eventType = "assistant_turn_final",
                    detailLevel = "p1",
                    clientType = "chrome",
                    modelId = "model-a",
                    message = "visible reply text for user",
                    payload = new { charCount = 999, truncated = false, activeModelId = "model-a" }
                }
            }
        };
        var json = JsonSerializer.Serialize(body);
        var res = await _client.PostAsync("/ingest/batch", new StringContent(json, Encoding.UTF8, "application/json"));
        res.EnsureSuccessStatusCode();
        var path = Path.Combine(_factory.DataRoot, "devices", deviceId, "sessions", sessionId + ".txt");
        Assert.True(File.Exists(path), "session file should exist: " + path);
        var text = await File.ReadAllTextAsync(path);
        Assert.Contains("assistant_turn_final", text, StringComparison.Ordinal);
        Assert.Contains("charCount=999", text, StringComparison.Ordinal);
        Assert.Contains("truncated=False", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("msgPreview=", text, StringComparison.Ordinal);
        Assert.Contains("visible reply", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Ingest_tool_invocation_end_minimal_includes_msgPreview()
    {
        var deviceId = Guid.NewGuid().ToString();
        var sessionId = "sess002";
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "test-ingest-key");
        var body = new
        {
            deviceId,
            clientTier = "minimal",
            events = new[]
            {
                new
                {
                    sessionId,
                    eventType = "tool_invocation_end",
                    detailLevel = "p0",
                    clientType = "chrome",
                    modelId = "",
                    message = "Word.save success=True len=42"
                }
            }
        };
        var res = await _client.PostAsync("/ingest/batch",
            new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"));
        res.EnsureSuccessStatusCode();
        var path = Path.Combine(_factory.DataRoot, "devices", deviceId, "sessions", sessionId + ".txt");
        var text = await File.ReadAllTextAsync(path);
        Assert.Contains("tool_invocation_end", text, StringComparison.Ordinal);
        Assert.Contains("msgPreview=", text, StringComparison.Ordinal);
        Assert.Contains("Word.save", text, StringComparison.Ordinal);
    }
}

using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using OfficeCopilot.Server;
using Xunit;

namespace backend.Tests.Integration;

public class ApiIntegrationTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly HttpClient _client;

    public ApiIntegrationTests(WebApplicationFactory<Program> factory)
    {
        _client = factory.WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment(Environments.Development);
            builder.ConfigureAppConfiguration((_, config) =>
            {
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["RagStorageType"] = "Memory",
                    ["PlansDirectory"] = "",
                    ["ScheduledTasksDirectory"] = "",
                });
            });
        }).CreateClient();
    }

    [Fact]
    public async Task Health_Returns200_WithStatus()
    {
        var response = await _client.GetAsync("/health");
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync();
        var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        Assert.True(root.TryGetProperty("status", out var status));
        Assert.Equal("running", status.GetString());
        Assert.True(root.TryGetProperty("time", out _));
    }

    [Fact]
    public async Task GetConfig_Returns200_WithCamelCaseFields()
    {
        var response = await _client.GetAsync("/api/config");
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync();
        var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        // API 契约：前端 camelCase，如 endpoint、modelId
        Assert.True(root.TryGetProperty("ai", out var ai));
        Assert.True(ai.TryGetProperty("endpoint", out _) || ai.TryGetProperty("provider", out _));
    }

    [Fact]
    public async Task PostConfigTestAi_MissingEndpoint_Returns200_WithOkFalse()
    {
        var body = new { modelId = "gpt-4", apiKey = "sk-x" };
        var content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        var response = await _client.PostAsync("/api/config/test-ai", content);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadAsStringAsync();
        var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        Assert.True(root.TryGetProperty("ok", out var ok));
        Assert.False(ok.GetBoolean());
        Assert.True(root.TryGetProperty("message", out var msg));
        Assert.NotNull(msg.GetString());
    }

    [Fact]
    public async Task PostConfigTestAi_EmptyBody_Returns200_WithOkFalse()
    {
        var content = new StringContent("{}", Encoding.UTF8, "application/json");
        var response = await _client.PostAsync("/api/config/test-ai", content);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadAsStringAsync();
        var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        Assert.True(root.TryGetProperty("ok", out var ok));
        Assert.False(ok.GetBoolean());
    }

    [Fact]
    public async Task GetToolsBuiltin_Returns200_WithArray()
    {
        var response = await _client.GetAsync("/api/tools/builtin");
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync();
        var list = JsonDocument.Parse(json).RootElement;
        Assert.Equal(JsonValueKind.Array, list.ValueKind);
        Assert.True(list.GetArrayLength() > 0);
        var first = list[0];
        Assert.True(first.TryGetProperty("id", out _));
        Assert.True(first.TryGetProperty("name", out _));
    }

    [Fact]
    public async Task GetPlans_Returns200_WithArray()
    {
        var response = await _client.GetAsync("/api/plans");
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync();
        var list = JsonDocument.Parse(json).RootElement;
        Assert.Equal(JsonValueKind.Array, list.ValueKind);
    }

    [Fact]
    public async Task GetScheduledTasks_Returns200_WithArray()
    {
        var response = await _client.GetAsync("/api/scheduled-tasks");
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync();
        var list = JsonDocument.Parse(json).RootElement;
        Assert.Equal(JsonValueKind.Array, list.ValueKind);
    }

    [Fact]
    public async Task GetMemory_Returns200_WithItemsOrOkFalse()
    {
        var response = await _client.GetAsync("/api/memory?skip=0&take=10");
        // 可能 200 + ok:true + items，或未配置 Embedding 时 400
        var json = await response.Content.ReadAsStringAsync();
        var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (response.StatusCode == HttpStatusCode.OK && root.TryGetProperty("ok", out var ok) && ok.GetBoolean())
            Assert.True(root.TryGetProperty("items", out _));
    }

    [Fact]
    public async Task PostTranscribe_NoFormContentType_Returns400_WithMessage()
    {
        var content = new StringContent("{}", Encoding.UTF8, "application/json");
        var response = await _client.PostAsync("/api/transcribe", content);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var json = await response.Content.ReadAsStringAsync();
        var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        Assert.True(root.TryGetProperty("ok", out var ok));
        Assert.False(ok.GetBoolean());
        Assert.True(root.TryGetProperty("message", out var msg));
        Assert.False(string.IsNullOrWhiteSpace(msg.GetString()));
    }

    [Fact]
    public async Task PostTranscribe_FormWithoutFile_ReturnsError_WithMessage()
    {
        using var form = new MultipartFormDataContent();
        var response = await _client.PostAsync("/api/transcribe", form);
        Assert.False(response.IsSuccessStatusCode, "Expected 4xx/5xx for request without file");
        var body = await response.Content.ReadAsStringAsync();
        if (body.TrimStart().StartsWith("{"))
        {
            var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            Assert.True(root.TryGetProperty("ok", out var ok));
            Assert.False(ok.GetBoolean());
            Assert.True(root.TryGetProperty("message", out var msg));
            Assert.False(string.IsNullOrWhiteSpace(msg.GetString()));
        }
    }

    [Fact]
    public async Task PostConfig_ValidBody_Returns200()
    {
        var body = new { ai = new { } };
        var content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        var response = await _client.PostAsync("/api/config", content);
        response.EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task GetSkills_Returns200_WithArray()
    {
        var response = await _client.GetAsync("/api/skills");
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync();
        var list = JsonDocument.Parse(json).RootElement;
        Assert.Equal(JsonValueKind.Array, list.ValueKind);
    }

    [Fact]
    public async Task GetAccurateData_Returns200_WithArray()
    {
        var response = await _client.GetAsync("/api/accurate-data");
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync();
        var list = JsonDocument.Parse(json).RootElement;
        Assert.Equal(JsonValueKind.Array, list.ValueKind);
    }
}

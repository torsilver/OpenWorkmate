using System.Net.Http;
using System.Net;
using System.IO;
using System;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.DependencyInjection;
using OfficeCopilot.Server;
using Xunit;

namespace backend.Tests.Integration;

public class ApiIntegrationTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly HttpClient _client;

    public ApiIntegrationTests(WebApplicationFactory<Program> factory)
    {
        var tempUserConfigPath = Path.Combine(
            Path.GetTempPath(),
            "OfficeCopilot.user-config-test-" + Guid.NewGuid().ToString("N") + ".json");

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
                    ["OfficeCopilot:UserConfigPath"] = tempUserConfigPath,
                });
            });
            builder.ConfigureServices(services =>
            {
                // 用于拦截 /api/config/test-stt 等外部 STT 请求，避免真实调用三方接口。
                services.AddHttpClient("STT")
                    .ConfigurePrimaryHttpMessageHandler(() => new SttFakeHttpMessageHandler());
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
    public async Task PostConfigTestStt_MissingEndpoint_Returns400_WithMessage()
    {
        var body = new { apiKey = "sk-x", modelId = "whisper-1" };
        var content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        var response = await _client.PostAsync("/api/config/test-stt", content);
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
    public async Task PostConfigTestStt_EmptyBody_Returns400_WithMessage()
    {
        var content = new StringContent("{}", Encoding.UTF8, "application/json");
        var response = await _client.PostAsync("/api/config/test-stt", content);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var json = await response.Content.ReadAsStringAsync();
        var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        Assert.True(root.TryGetProperty("ok", out var ok));
        Assert.False(ok.GetBoolean());
        Assert.True(root.TryGetProperty("message", out var msg));
    }

    [Fact]
    public async Task PostConfigTestStt_DashScopeCompatible_ReturnsOkTrue()
    {
        await SttFakeHttpMessageHandler.Mutex.WaitAsync();
        try
        {
            SttFakeHttpMessageHandler.LastRequestUri = null;
            SttFakeHttpMessageHandler.LastRequestBody = null;
            SttFakeHttpMessageHandler.OnSendAsync = _ =>
            {
                var responseText = """
                {
                  "choices": [
                    { "message": { "content": "识别结果" } }
                  ]
                }
                """;
                var resp = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(responseText, Encoding.UTF8, "application/json")
                };
                return Task.FromResult(resp);
            };

            var body = new { endpoint = "https://dashscope.aliyuncs.com/compatible-mode/v1", apiKey = "sk-x", modelId = "qwen3-asr-flash" };
            var content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

            var response = await _client.PostAsync("/api/config/test-stt", content);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            var json = await response.Content.ReadAsStringAsync();
            var doc = JsonDocument.Parse(json);
            Assert.True(doc.RootElement.GetProperty("ok").GetBoolean());
            var message = doc.RootElement.GetProperty("message").GetString() ?? "";
            Assert.Contains("DashScope", message, StringComparison.OrdinalIgnoreCase);

            Assert.NotNull(SttFakeHttpMessageHandler.LastRequestUri);
            Assert.EndsWith("/chat/completions", SttFakeHttpMessageHandler.LastRequestUri, StringComparison.OrdinalIgnoreCase);

            Assert.NotNull(SttFakeHttpMessageHandler.LastRequestBody);
            using var reqDoc = JsonDocument.Parse(SttFakeHttpMessageHandler.LastRequestBody!);
            Assert.Equal("qwen3-asr-flash", reqDoc.RootElement.GetProperty("model").GetString());
            var messages = reqDoc.RootElement.GetProperty("messages");
            var content0 = messages[0].GetProperty("content")[0];
            Assert.Equal("input_audio", content0.GetProperty("type").GetString());
        }
        finally
        {
            SttFakeHttpMessageHandler.OnSendAsync = null;
            SttFakeHttpMessageHandler.Mutex.Release();
        }
    }

    [Fact]
    public async Task PostConfigTestStt_DashScopeCompatible_Upstream404_ReturnsOkFalse()
    {
        await SttFakeHttpMessageHandler.Mutex.WaitAsync();
        try
        {
            SttFakeHttpMessageHandler.LastRequestUri = null;
            SttFakeHttpMessageHandler.LastRequestBody = null;
            SttFakeHttpMessageHandler.OnSendAsync = _ =>
            {
                var resp = new HttpResponseMessage(HttpStatusCode.NotFound)
                {
                    ReasonPhrase = "Not Found",
                    Content = new StringContent("not found", Encoding.UTF8, "text/plain")
                };
                return Task.FromResult(resp);
            };

            var body = new { endpoint = "https://dashscope.aliyuncs.com/compatible-mode/v1", apiKey = "sk-x", modelId = "qwen3-asr-flash" };
            var content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

            var response = await _client.PostAsync("/api/config/test-stt", content);
            Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);

            var json = await response.Content.ReadAsStringAsync();
            var doc = JsonDocument.Parse(json);
            Assert.False(doc.RootElement.GetProperty("ok").GetBoolean());
            var message = doc.RootElement.GetProperty("message").GetString() ?? "";
            Assert.Contains("请求失败", message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("404", message);
        }
        finally
        {
            SttFakeHttpMessageHandler.OnSendAsync = null;
            SttFakeHttpMessageHandler.Mutex.Release();
        }
    }

    [Fact]
    public async Task PostConfigTestOcr_MissingEndpoint_Returns400_WithMessage()
    {
        var body = new { apiKey = "key" };
        var content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        var response = await _client.PostAsync("/api/config/test-ocr", content);
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
    public async Task PostConfigTestOcr_EmptyBody_Returns400_WithMessage()
    {
        var content = new StringContent("{}", Encoding.UTF8, "application/json");
        var response = await _client.PostAsync("/api/config/test-ocr", content);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var json = await response.Content.ReadAsStringAsync();
        var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        Assert.True(root.TryGetProperty("ok", out var ok));
        Assert.False(ok.GetBoolean());
        Assert.True(root.TryGetProperty("message", out var msg));
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
    public async Task PostConfig_MissingEmbeddingModels_DoesNotClearExisting()
    {
        var embeddingModel = new
        {
            id = "emb-test-1",
            displayName = "Test Embedding",
            source = "Remote",
            endpoint = "https://example.com/v1",
            apiKey = "key",
            modelId = "text-embedding-3-small"
        };

        var initBody = new
        {
            ai = new { },
            embeddingModels = new[] { embeddingModel },
            activeEmbeddingModelId = "emb-test-1"
        };
        var initContent = new StringContent(JsonSerializer.Serialize(initBody), Encoding.UTF8, "application/json");
        var initResponse = await _client.PostAsync("/api/config", initContent);
        initResponse.EnsureSuccessStatusCode();

        var secondBody = new { ai = new { } };
        var secondContent = new StringContent(JsonSerializer.Serialize(secondBody), Encoding.UTF8, "application/json");
        var secondResponse = await _client.PostAsync("/api/config", secondContent);
        secondResponse.EnsureSuccessStatusCode();

        var getResponse = await _client.GetAsync("/api/config");
        getResponse.EnsureSuccessStatusCode();
        var json = await getResponse.Content.ReadAsStringAsync();
        var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.True(root.TryGetProperty("embeddingModels", out var embeddingModels));
        Assert.Equal(JsonValueKind.Array, embeddingModels.ValueKind);
        Assert.Equal(1, embeddingModels.GetArrayLength());
        Assert.True(embeddingModels[0].TryGetProperty("id", out var embId));
        Assert.Equal("emb-test-1", embId.GetString());

        Assert.True(root.TryGetProperty("activeEmbeddingModelId", out var activeEmbId));
        Assert.Equal("emb-test-1", activeEmbId.GetString());
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

    [Fact]
    public async Task GetDebugAgentStats_Returns200_WithCamelCaseShape()
    {
        var response = await _client.GetAsync("/api/debug/agent-stats");
        response.EnsureSuccessStatusCode();
        var root = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.True(root.TryGetProperty("serverStartedUtc", out _));
        Assert.True(root.TryGetProperty("toolSelection", out var ts));
        Assert.True(ts.TryGetProperty("totalNonPlanSelections", out _));
        Assert.True(root.TryGetProperty("toolInvocations", out var inv));
        Assert.Equal(JsonValueKind.Array, inv.ValueKind);
        Assert.True(root.TryGetProperty("toolSearchConfig", out var tsc));
        Assert.True(tsc.TryGetProperty("toolSearchTopK", out _));
        Assert.True(tsc.TryGetProperty("toolSearchMinScore", out _));
        Assert.True(tsc.TryGetProperty("toolSearchMinCount", out _));
        Assert.True(root.TryGetProperty("statsAccumulatedSinceUtc", out _));
    }

    [Fact]
    public async Task PostDebugAgentStatsReset_Returns200_WithOk_AndClearsCounters()
    {
        var reset = await _client.PostAsync("/api/debug/agent-stats/reset", null);
        reset.EnsureSuccessStatusCode();
        var resetRoot = JsonDocument.Parse(await reset.Content.ReadAsStringAsync()).RootElement;
        Assert.True(resetRoot.TryGetProperty("ok", out var ok) && ok.GetBoolean());

        var get = await _client.GetAsync("/api/debug/agent-stats");
        get.EnsureSuccessStatusCode();
        var ts = JsonDocument.Parse(await get.Content.ReadAsStringAsync()).RootElement.GetProperty("toolSelection");
        Assert.Equal(0, ts.GetProperty("totalNonPlanSelections").GetInt64());
        Assert.Equal(0, ts.GetProperty("vectorSearchRunCount").GetInt64());
    }
}

internal sealed class SttFakeHttpMessageHandler : HttpMessageHandler
{
    public static readonly SemaphoreSlim Mutex = new(1, 1);

    public static Func<HttpRequestMessage, Task<HttpResponseMessage>>? OnSendAsync;

    public static string? LastRequestUri;
    public static string? LastRequestBody;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        LastRequestUri = request.RequestUri?.ToString();
        if (request.Content != null)
            LastRequestBody = await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        else
            LastRequestBody = null;

        var handler = OnSendAsync;
        if (handler == null)
            return new HttpResponseMessage(HttpStatusCode.InternalServerError) { Content = new StringContent("No fake handler") };
        return await handler(request).ConfigureAwait(false);
    }
}

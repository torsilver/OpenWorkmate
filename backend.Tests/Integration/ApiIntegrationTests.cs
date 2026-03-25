using System.Net.Http;
using System.Net;
using System.IO;
using System.Linq;
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
                    ["WebSocket:AuthToken"] = "",
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
    public async Task PostConfigTestStt_InvalidConnectionKind_Returns400()
    {
        var body = new
        {
            endpoint = "https://api.openai.com/v1",
            apiKey = "k",
            modelId = "whisper-1",
            connectionKind = "not_a_valid_kind"
        };
        var content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        var response = await _client.PostAsync("/api/config/test-stt", content);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task PostConfigTestOcr_InvalidConnectionKind_Returns400()
    {
        var body = new
        {
            endpoint = "https://api.openai.com/v1",
            apiKey = "k",
            connectionKind = "invalid_ocr_kind"
        };
        var content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        var response = await _client.PostAsync("/api/config/test-ocr", content);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task PostConfigTestStt_DashScopeCompatible_CustomModelId_SentInUpstreamJson()
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
                    { "message": { "content": "x" } }
                  ]
                }
                """;
                var resp = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(responseText, Encoding.UTF8, "application/json")
                };
                return Task.FromResult(resp);
            };

            var body = new
            {
                endpoint = "https://dashscope.aliyuncs.com/compatible-mode/v1",
                apiKey = "sk-x",
                modelId = "my-custom-asr-model",
                connectionKind = "dashscope_openai_chat_audio"
            };
            var content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

            var response = await _client.PostAsync("/api/config/test-stt", content);
            response.EnsureSuccessStatusCode();
            Assert.NotNull(SttFakeHttpMessageHandler.LastRequestBody);
            using var reqDoc = JsonDocument.Parse(SttFakeHttpMessageHandler.LastRequestBody!);
            Assert.Equal("my-custom-asr-model", reqDoc.RootElement.GetProperty("model").GetString());
        }
        finally
        {
            SttFakeHttpMessageHandler.OnSendAsync = null;
            SttFakeHttpMessageHandler.Mutex.Release();
        }
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
    public async Task PostScheduledTasks_IntervalSeconds_Returns200_WithIdAndNextRunAt()
    {
        var payload = new
        {
            title = "t-int-sec",
            content = "ping",
            scheduleType = "interval",
            intervalSeconds = 42
        };
        var body = JsonSerializer.Serialize(payload, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        using var content = new StringContent(body, Encoding.UTF8, "application/json");
        var response = await _client.PostAsync("/api/scheduled-tasks", content);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync();
        var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        Assert.True(root.TryGetProperty("ok", out var ok) && ok.GetBoolean());
        Assert.True(root.TryGetProperty("id", out var idEl));
        Assert.False(string.IsNullOrWhiteSpace(idEl.GetString()));

        var getOne = await _client.GetAsync("/api/scheduled-tasks/" + Uri.EscapeDataString(idEl.GetString()!));
        getOne.EnsureSuccessStatusCode();
        var getJson = await getOne.Content.ReadAsStringAsync();
        var getDoc = JsonDocument.Parse(getJson);
        var meta = getDoc.RootElement.GetProperty("meta");
        Assert.True(meta.TryGetProperty("intervalSeconds", out var isec));
        Assert.Equal(42, isec.GetInt32());
        Assert.True(meta.TryGetProperty("nextRunAt", out var nra));
        Assert.NotEqual(JsonValueKind.Null, nra.ValueKind);
    }

    [Fact]
    public async Task PostScheduledTasks_Once_WithIntervalSeconds_Returns200_AndDeleteAfterRunTrue()
    {
        var payload = new
        {
            title = "t-once-delay",
            content = "once",
            scheduleType = "once",
            intervalSeconds = 77
        };
        var body = JsonSerializer.Serialize(payload, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        using var content = new StringContent(body, Encoding.UTF8, "application/json");
        var response = await _client.PostAsync("/api/scheduled-tasks", content);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync();
        var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        Assert.True(root.TryGetProperty("id", out var idEl));
        var id = idEl.GetString()!;

        var getOne = await _client.GetAsync("/api/scheduled-tasks/" + Uri.EscapeDataString(id));
        getOne.EnsureSuccessStatusCode();
        var getJson = await getOne.Content.ReadAsStringAsync();
        var meta = JsonDocument.Parse(getJson).RootElement.GetProperty("meta");
        Assert.True(meta.TryGetProperty("scheduleType", out var st));
        Assert.Equal("once", st.GetString());
        Assert.True(meta.TryGetProperty("deleteAfterRun", out var dar));
        Assert.True(dar.GetBoolean());
    }

    [Fact]
    public async Task PostScheduledTasks_Once_WithoutTrigger_Returns400_WithMessage()
    {
        var payload = new { title = "t-once-bad", content = "x", scheduleType = "once" };
        var body = JsonSerializer.Serialize(payload, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        using var content = new StringContent(body, Encoding.UTF8, "application/json");
        var response = await _client.PostAsync("/api/scheduled-tasks", content);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var json = await response.Content.ReadAsStringAsync();
        var root = JsonDocument.Parse(json).RootElement;
        Assert.True(root.TryGetProperty("message", out var msg));
        Assert.False(string.IsNullOrWhiteSpace(msg.GetString()));
    }

    [Fact]
    public async Task PutScheduledTasks_BecomeOnce_WithoutFire_Returns400_WithMessage()
    {
        var createPayload = new
        {
            title = "t-to-once",
            content = "c",
            scheduleType = "interval",
            intervalSeconds = 600
        };
        var createBody = JsonSerializer.Serialize(createPayload, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        using var createContent = new StringContent(createBody, Encoding.UTF8, "application/json");
        var createRes = await _client.PostAsync("/api/scheduled-tasks", createContent);
        createRes.EnsureSuccessStatusCode();
        var id = JsonDocument.Parse(await createRes.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetString()!;

        var putPayload = new { scheduleType = "once" };
        var putBody = JsonSerializer.Serialize(putPayload, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        using var putContent = new StringContent(putBody, Encoding.UTF8, "application/json");
        var putRes = await _client.PutAsync("/api/scheduled-tasks/" + Uri.EscapeDataString(id), putContent);
        Assert.Equal(HttpStatusCode.BadRequest, putRes.StatusCode);
        var putJson = await putRes.Content.ReadAsStringAsync();
        var root = JsonDocument.Parse(putJson).RootElement;
        Assert.True(root.TryGetProperty("message", out var msg));
        Assert.Contains("once", msg.GetString()!, StringComparison.OrdinalIgnoreCase);
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
    public async Task PostConfig_UiThemeId_RoundTrip()
    {
        var body = new { ai = new { }, uiThemeId = "minimal" };
        var content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        var postResponse = await _client.PostAsync("/api/config", content);
        postResponse.EnsureSuccessStatusCode();

        var getResponse = await _client.GetAsync("/api/config");
        getResponse.EnsureSuccessStatusCode();
        var json = await getResponse.Content.ReadAsStringAsync();
        var root = JsonDocument.Parse(json).RootElement;
        Assert.True(root.TryGetProperty("uiThemeId", out var themeProp));
        Assert.Equal("minimal", themeProp.GetString());
    }

    [Fact]
    public async Task PostConfig_OmitsUiThemeId_KeepsPrevious()
    {
        var initBody = new { ai = new { }, uiThemeId = "lines" };
        var initContent = new StringContent(JsonSerializer.Serialize(initBody), Encoding.UTF8, "application/json");
        (await _client.PostAsync("/api/config", initContent)).EnsureSuccessStatusCode();

        var secondBody = new { ai = new { } };
        var secondContent = new StringContent(JsonSerializer.Serialize(secondBody), Encoding.UTF8, "application/json");
        (await _client.PostAsync("/api/config", secondContent)).EnsureSuccessStatusCode();

        var getResponse = await _client.GetAsync("/api/config");
        getResponse.EnsureSuccessStatusCode();
        var json = await getResponse.Content.ReadAsStringAsync();
        var root = JsonDocument.Parse(json).RootElement;
        Assert.True(root.TryGetProperty("uiThemeId", out var themeProp));
        Assert.Equal("lines", themeProp.GetString());
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

    [Fact]
    public async Task GetDebugLogFiles_Returns200_WithFilesArray()
    {
        var response = await _client.GetAsync("/api/debug/log-files");
        response.EnsureSuccessStatusCode();
        var root = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.True(root.TryGetProperty("files", out var files));
        Assert.Equal(JsonValueKind.Array, files.ValueKind);
    }

    [Fact]
    public async Task GetDebugLogTail_WithSyntheticFile_ReturnsLines()
    {
        var logsDir = Path.Combine(Directory.GetCurrentDirectory(), "logs");
        Directory.CreateDirectory(logsDir);
        const string name = "office-copilot-20990101.txt";
        var path = Path.Combine(logsDir, name);
        await File.WriteAllTextAsync(path, "alpha\nbeta\ngamma\n");
        try
        {
            var response = await _client.GetAsync("/api/debug/log-tail?file=" + Uri.EscapeDataString(name) + "&lines=10");
            response.EnsureSuccessStatusCode();
            var root = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
            Assert.True(root.TryGetProperty("lines", out var lines));
            Assert.True(lines.GetArrayLength() >= 3);
            var joined = string.Join("\n", lines.EnumerateArray().Select(e => e.GetString() ?? ""));
            Assert.Contains("gamma", joined, StringComparison.Ordinal);
            Assert.Contains("alpha", joined, StringComparison.Ordinal);
        }
        finally
        {
            try
            {
                File.Delete(path);
            }
            catch
            {
                /* ignore */
            }
        }
    }

    [Fact]
    public async Task PostMeetingTranscriptSegment_ThenMeta_ReturnsOkAndTotalChars()
    {
        var sid = "testmt_" + Guid.NewGuid().ToString("N")[..12];
        var body1 = new { sessionId = sid, sequence = 0, text = "hello" };
        var r1 = await _client.PostAsync(
            "/api/meeting-transcript/segment",
            new StringContent(JsonSerializer.Serialize(body1), Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.OK, r1.StatusCode);
        var j1 = await r1.Content.ReadAsStringAsync();
        Assert.Contains("\"ok\":true", j1, StringComparison.Ordinal);

        var body2 = new { sessionId = sid, sequence = 1, text = "world" };
        var r2 = await _client.PostAsync(
            "/api/meeting-transcript/segment",
            new StringContent(JsonSerializer.Serialize(body2), Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.OK, r2.StatusCode);

        var rMeta = await _client.GetAsync("/api/meeting-transcript/" + Uri.EscapeDataString(sid) + "/meta");
        Assert.Equal(HttpStatusCode.OK, rMeta.StatusCode);
        var doc = JsonDocument.Parse(await rMeta.Content.ReadAsStringAsync());
        var root = doc.RootElement;
        Assert.True(root.TryGetProperty("ok", out var ok) && ok.GetBoolean());
        Assert.True(root.TryGetProperty("totalChars", out var tc));
        Assert.True(tc.GetInt32() > 0);

        try
        {
            var path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "OfficeCopilot", "MeetingTranscripts", sid + ".jsonl");
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            /* ignore */
        }
    }

    [Fact]
    public async Task GetMeetingTranscriptSegments_AfterSeq_ReturnsIncremental()
    {
        var sid = "testmtseg_" + Guid.NewGuid().ToString("N")[..10];
        var body0 = new { sessionId = sid, sequence = 0, text = "alpha" };
        Assert.Equal(HttpStatusCode.OK, (await _client.PostAsync(
            "/api/meeting-transcript/segment",
            new StringContent(JsonSerializer.Serialize(body0), Encoding.UTF8, "application/json"))).StatusCode);
        var body1 = new { sessionId = sid, sequence = 1, text = "beta" };
        Assert.Equal(HttpStatusCode.OK, (await _client.PostAsync(
            "/api/meeting-transcript/segment",
            new StringContent(JsonSerializer.Serialize(body1), Encoding.UTF8, "application/json"))).StatusCode);

        var rAll = await _client.GetAsync("/api/meeting-transcript/" + Uri.EscapeDataString(sid) + "/segments?afterSeq=-1");
        Assert.Equal(HttpStatusCode.OK, rAll.StatusCode);
        var docAll = JsonDocument.Parse(await rAll.Content.ReadAsStringAsync());
        var rootAll = docAll.RootElement;
        Assert.True(rootAll.TryGetProperty("segments", out var segs) && segs.GetArrayLength() == 2);

        var rAfter = await _client.GetAsync("/api/meeting-transcript/" + Uri.EscapeDataString(sid) + "/segments?afterSeq=0");
        Assert.Equal(HttpStatusCode.OK, rAfter.StatusCode);
        var docAfter = JsonDocument.Parse(await rAfter.Content.ReadAsStringAsync());
        Assert.True(docAfter.RootElement.TryGetProperty("segments", out var segs2) && segs2.GetArrayLength() == 1);
        Assert.True(segs2[0].TryGetProperty("sequence", out var sq) && sq.GetInt32() == 1);

        try
        {
            var path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "OfficeCopilot", "MeetingTranscripts", sid + ".jsonl");
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            /* ignore */
        }
    }

    [Fact]
    public async Task PostMeetingTranscriptSegment_InvalidSessionId_Returns400()
    {
        var body = new { sessionId = "bad id!", sequence = 0, text = "x" };
        var r = await _client.PostAsync(
            "/api/meeting-transcript/segment",
            new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        var json = await r.Content.ReadAsStringAsync();
        var doc = JsonDocument.Parse(json);
        Assert.True(doc.RootElement.TryGetProperty("ok", out var ok));
        Assert.False(ok.GetBoolean());
        Assert.True(doc.RootElement.TryGetProperty("message", out _));
    }
}

public sealed class SttFakeHttpMessageHandler : HttpMessageHandler
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

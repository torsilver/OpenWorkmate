using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using OfficeCopilot.Server;
using OfficeCopilot.Server.Plugins;
using OfficeCopilot.Server.Services;
using OfficeCopilot.Server.Services.Memory;
using OfficeCopilot.Server.Services.Plan;
using OfficeCopilot.Server.Services.ScheduledTask;
using OfficeCopilot.Server.Services.CrossAgentTask;
using OfficeCopilot.Server.Services.Stt;
using OfficeCopilot.Server.Services.Ocr;
using OfficeCopilot.Server.Mcp;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog;

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Debug()
    .WriteTo.Console()
    .WriteTo.File("logs/office-copilot-.txt", rollingInterval: RollingInterval.Day)
    .CreateLogger();

try
{
    var builder = WebApplication.CreateBuilder(args);
    var buildToolIndex = args.Contains("--build-tool-index");
    builder.Host.UseSerilog();

    builder.Services.AddHttpClient();
    builder.Services.AddSingleton<ConfigService>();
    builder.Services.AddSingleton<SkillService>();
    builder.Services.AddSingleton<ClawhubScriptRunner>();
    builder.Services.AddSingleton<McpClientManager>();
    builder.Services.AddSingleton<IKernelAccessor, KernelAccessor>();
    builder.Services.AddSingleton<EmbeddingProvider>();
    builder.Services.AddSingleton<IEmbeddingProvider>(sp => sp.GetRequiredService<EmbeddingProvider>());
    builder.Services.AddSingleton<IVectorStore>(sp =>
    {
        if (buildToolIndex)
        {
            var dataDir = Path.Combine(Directory.GetCurrentDirectory(), "Data");
            Directory.CreateDirectory(dataDir);
            var dbPath = Path.Combine(dataDir, "rag.db");
            return new SqliteVectorStore("Data Source=" + dbPath);
        }
        var config = sp.GetRequiredService<ConfigService>().Current;
        var t = (config.RagStorageType ?? "").Trim();
        var path = (config.RagStoragePath ?? "").Trim();
        if (string.Equals(t, "Sqlite", StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrEmpty(path))
                path = "rag.db";
            path = Environment.ExpandEnvironmentVariables(path);
            if (!Path.IsPathRooted(path))
                path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OfficeCopilot", path);
            return new SqliteVectorStore("Data Source=" + path);
        }
        return new InMemoryVectorStore();
    });
    builder.Services.AddSingleton<IMemoryStoreService, MemoryStoreService>();
    builder.Services.AddSingleton<IToolSelector, ToolSelectionService>();
    builder.Services.AddSingleton<IToolIndexService, ToolIndexService>();
    builder.Services.AddSingleton<SessionManager>();
    builder.Services.AddSingleton<RpcManager>();
    builder.Services.AddSingleton<HitlManager>();
builder.Services.AddSingleton<UserOptionsManager>();
    builder.Services.AddSingleton<ScreenshotCacheService>();
    builder.Services.AddSingleton<AttachmentCacheService>();
    builder.Services.AddSingleton<SttTranscriberProvider>();
    builder.Services.AddSingleton<OcrExtractorProvider>();
    builder.Services.AddSingleton<ITranscribeService, TranscribeService>();
    builder.Services.AddSingleton<IOcrService, OcrService>();
    builder.Services.AddSingleton<StreamCancelService>();
    builder.Services.AddSingleton<ContextManager>();
    builder.Services.AddSingleton<AgentDebugStatsService>(sp => new AgentDebugStatsService(
        sp.GetRequiredService<ILogger<AgentDebugStatsService>>(),
        sp.GetRequiredService<IHostApplicationLifetime>()));
    builder.Services.AddSingleton<ChatService>();
    builder.Services.AddSingleton<IPlanStore>(sp =>
    {
        var config = sp.GetRequiredService<ConfigService>().Current;
        var dir = (config.PlansDirectory ?? "").Trim();
        if (string.IsNullOrEmpty(dir))
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            dir = Path.Combine(appData, "OfficeCopilot", "Plans");
        }
        else
        {
            dir = Environment.ExpandEnvironmentVariables(dir);
            if (!Path.IsPathRooted(dir))
                dir = Path.Combine(AppContext.BaseDirectory, dir);
        }
        return new FilePlanStore(dir, sp.GetRequiredService<ILogger<FilePlanStore>>());
    });
    builder.Services.AddSingleton<PlanPlugin>();
    builder.Services.AddSingleton<SkillAuthorPlugin>();
    builder.Services.AddSingleton<ICrossAgentTaskStore>(sp =>
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var dir = Path.Combine(appData, "OfficeCopilot");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "CrossAgentTasks.db");
        return new SqliteCrossAgentTaskStore("Data Source=" + path);
    });
    builder.Services.AddSingleton<IScheduledTaskStore>(sp =>
    {
        var config = sp.GetRequiredService<ConfigService>().Current;
        var dir = (config.ScheduledTasksDirectory ?? "").Trim();
        if (string.IsNullOrEmpty(dir))
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            dir = Path.Combine(appData, "OfficeCopilot", "ScheduledTasks");
        }
        else
        {
            dir = Environment.ExpandEnvironmentVariables(dir);
            if (!Path.IsPathRooted(dir))
                dir = Path.Combine(AppContext.BaseDirectory, dir);
        }
        return new FileScheduledTaskStore(dir, sp.GetRequiredService<ILogger<FileScheduledTaskStore>>());
    });

    builder.Services.AddCors();
    builder.Services.AddHostedService<ScheduledTaskRunnerService>();

    var app = builder.Build();

    if (buildToolIndex)
    {
        var configPath = Path.Combine(Directory.GetCurrentDirectory(), "tool-index-build.json");
        if (!File.Exists(configPath))
            configPath = Path.Combine(AppContext.BaseDirectory, "tool-index-build.json");
        if (!File.Exists(configPath))
        {
            Log.Error("tool-index-build.json not found in current directory or app base. Create it with endpoint, apiKey, modelId (all required).");
            Environment.Exit(1);
        }
        string endpoint = "", apiKey = "", modelId = "";
        try
        {
            var json = await File.ReadAllTextAsync(configPath);
            var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            endpoint = root.TryGetProperty("endpoint", out var ep) ? ep.GetString() ?? "" : "";
            apiKey = root.TryGetProperty("apiKey", out var ak) ? ak.GetString() ?? "" : "";
            modelId = root.TryGetProperty("modelId", out var mi) ? mi.GetString() ?? "" : "";
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to read tool-index-build.json");
            Environment.Exit(1);
        }
        if (string.IsNullOrWhiteSpace(endpoint) || string.IsNullOrWhiteSpace(apiKey) || string.IsNullOrWhiteSpace(modelId))
        {
            Log.Error("tool-index-build.json must contain endpoint, apiKey, modelId (all required).");
            Environment.Exit(1);
        }
        var configService = app.Services.GetRequiredService<ConfigService>();
        var skillService = app.Services.GetRequiredService<SkillService>();
        configService.SetMinimalConfigForToolIndexBuild(endpoint.Trim(), apiKey.Trim(), modelId.Trim());
        skillService.SetReturnEmptySkillsForToolIndexBuild(true);
        using (var scope = app.Services.CreateScope())
        {
            var chat = scope.ServiceProvider.GetRequiredService<ChatService>();
            await chat.RebuildKernelAsync(skipUserToolIndexSync: true);
            var kernelAccessor = scope.ServiceProvider.GetRequiredService<IKernelAccessor>();
            var kernel = kernelAccessor.Kernel;
            if (kernel == null)
            {
                Log.Error("Kernel is null after RebuildKernelAsync.");
                Environment.Exit(1);
            }
            var toolIndex = scope.ServiceProvider.GetRequiredService<IToolIndexService>();
            await toolIndex.BuildIndexAsync(kernel, ToolIndexBuildMode.BuiltinOnly);

            // 阶段 2：真实用户配置 + Skills + 可连接的 MCP，用户工具索引（与运行时增量逻辑一致）
            skillService.SetReturnEmptySkillsForToolIndexBuild(false);
            configService.ReloadConfigFromDisk();
            configService.ApplyToolIndexBuildEmbeddingCredentials(endpoint.Trim(), apiKey.Trim(), modelId.Trim());
            await chat.RebuildKernelAsync(skipUserToolIndexSync: true);
            kernel = kernelAccessor.Kernel;
            if (kernel == null)
            {
                Log.Error("Kernel is null after phase-2 RebuildKernelAsync.");
                Environment.Exit(1);
            }
            await toolIndex.SyncUserToolIndexAsync(kernel);
        }
        Log.Information("Tool index built successfully (builtin + user tools). DB: {Path}", Path.Combine(Directory.GetCurrentDirectory(), "Data", "rag.db"));
        Environment.Exit(0);
    }

app.UseWebSockets(new WebSocketOptions
{
    KeepAliveInterval = TimeSpan.FromSeconds(30)
});

var wsConfig = app.Configuration.GetSection("WebSocket");
var wsPath = wsConfig["Path"] ?? "/ws";
var authToken = wsConfig["AuthToken"] ?? "";
var allowedOrigins = wsConfig.GetSection("AllowedOrigins").Get<string[]>() ?? [];
const string DevToken = "office-copilot-dev-token";
var isDev = app.Environment.IsDevelopment();
// 开发环境下始终放行 localhost，便于 wpsjs debug 等本地任务窗格连接
if (isDev)
{
    var devOrigins = new[] { "http://127.0.0.1", "http://localhost" };
    allowedOrigins = allowedOrigins.Union(devOrigins).Distinct().ToArray();
}

app.UseCors(policy => 
    policy.WithOrigins(allowedOrigins.Length > 0 ? allowedOrigins : new[] { "*" })
          .AllowAnyMethod()
          .AllowAnyHeader()
          .SetIsOriginAllowed(origin => true) // Local tool, allow all origins for now to avoid extension CORS issues
);

app.Map(wsPath, async (HttpContext context, SessionManager sessions, ChatService chatService) =>
{
    if (!context.WebSockets.IsWebSocketRequest)
    {
        context.Response.StatusCode = 400;
        await context.Response.WriteAsync("WebSocket connections only.");
        return;
    }

    var origin = context.Request.Headers.Origin.ToString();
    if (allowedOrigins.Length > 0
        && !string.IsNullOrEmpty(origin)
        && !allowedOrigins.Any(o => origin.StartsWith(o, StringComparison.OrdinalIgnoreCase)))
    {
        app.Logger.LogWarning("Rejected connection from origin: {Origin}", origin);
        context.Response.StatusCode = 403;
        await context.Response.WriteAsync("Forbidden: invalid origin.");
        return;
    }

    var token = context.Request.Query["token"].ToString();
    var tokenOk = string.IsNullOrEmpty(authToken)
        || token == authToken
        || (isDev && token == DevToken);
    if (!tokenOk)
    {
        app.Logger.LogWarning("Rejected connection: invalid token");
        context.Response.StatusCode = 401;
        await context.Response.WriteAsync("Unauthorized: invalid token.");
        return;
    }

    var sessionId = context.Request.Query["sessionId"].ToString();
    if (string.IsNullOrEmpty(sessionId))
        sessionId = Guid.NewGuid().ToString("N")[..8];

    var clientType = context.Request.Query["clientType"].ToString().Trim();
    if (string.IsNullOrEmpty(clientType))
        clientType = null;

    using var ws = await context.WebSockets.AcceptWebSocketAsync();
    app.Logger.LogInformation("Session {SessionId} connected clientType={ClientType}", sessionId, clientType ?? "(none)");

    sessions.Add(sessionId, ws, clientType);
    try
    {
        var rpcManager = app.Services.GetRequiredService<RpcManager>();
        var hitlManager = app.Services.GetRequiredService<HitlManager>();
        var userOptionsManager = app.Services.GetRequiredService<UserOptionsManager>();
        var streamCancelService = app.Services.GetRequiredService<StreamCancelService>();
        var attachmentCache = app.Services.GetRequiredService<AttachmentCacheService>();
        await HandleSession(ws, sessionId, sessions, chatService, rpcManager, hitlManager, userOptionsManager, streamCancelService, attachmentCache, app.Logger);
    }
    finally
    {
        sessions.Remove(sessionId);
        app.Logger.LogInformation("Session {SessionId} disconnected", sessionId);
    }
});

app.MapGet("/health", () => Results.Ok(new { status = "running", time = DateTime.Now }));

app.MapGet("/api/debug/agent-stats", (AgentDebugStatsService agentDebugStats, ConfigService config) =>
{
    var snap = agentDebugStats.GetSnapshot();
    var cw = config.Current.ContextWindow ?? new ContextWindowConfig();
    var withConfig = new AgentDebugStatsResponse
    {
        ServerStartedUtc = snap.ServerStartedUtc,
        StatsAccumulatedSinceUtc = snap.StatsAccumulatedSinceUtc,
        ToolSelection = snap.ToolSelection,
        ToolInvocations = snap.ToolInvocations,
        ToolSearchConfig = new ToolSearchConfigSnapshotDto
        {
            ToolSearchTopK = cw.ToolSearchTopK,
            ToolSearchMinScore = cw.ToolSearchMinScore,
            ToolSearchMinCount = cw.ToolSearchMinCount
        }
    };
    return Results.Json(withConfig, JsonCtx.Default.AgentDebugStatsResponse);
});
app.MapPost("/api/debug/agent-stats/reset", (AgentDebugStatsService agentDebugStats) =>
{
    agentDebugStats.Reset();
    return Results.Json(new DebugStatsResetResponse(), JsonCtx.Default.DebugStatsResetResponse);
});

app.Logger.LogInformation("WebSocket path={Path}, AuthRequired={Auth}, DevTokenAccepted={Dev}, AllowedOriginsCount={Count}",
    wsPath, !string.IsNullOrEmpty(authToken), isDev, allowedOrigins.Length);

app.MapGet("/api/config", (ConfigService config) => Results.Json(config.Current, JsonCtx.Default.AppConfig));
app.MapPost("/api/config", async (HttpContext ctx, ConfigService config) =>
{
    // 与前端 camelCase 一致，使用与 LoadConfig 相同的反序列化选项（含 embeddingModels[].endpoint）
    var newConfig = await JsonSerializer.DeserializeAsync<AppConfig>(ctx.Request.Body, ConfigService.AppConfigDeserializeOptions);
    if (newConfig != null)
    {
        config.SaveConfig(newConfig);
        return Results.Ok();
    }
    return Results.Json(new { ok = false, message = "请求体解析失败或格式无效，请确认发送的是有效 JSON 配置。" }, statusCode: 400);
});

app.MapPost("/api/config/test-ai", async (HttpContext ctx, ILogger<Program> logger) =>
{
    TestAiRequest? body;
    try
    {
        body = await JsonSerializer.DeserializeAsync<TestAiRequest>(ctx.Request.Body, JsonCtx.Default.TestAiRequest);
    }
    catch (Exception ex)
    {
        logger.LogWarning(ex, "Test AI: request body deserialize failed");
        return Results.Json(new { ok = false, message = "请求体解析失败，请确认发送的是 JSON 且字段为 endpoint、modelId、apiKey、provider、deploymentName（小写驼峰）。" }, statusCode: 200);
    }
    if (body == null || string.IsNullOrWhiteSpace(body.Endpoint) || string.IsNullOrWhiteSpace(body.ModelId))
    {
        return Results.Json(new { ok = false, message = "请求参数无效：缺少或为空 endpoint 或 modelId，请检查请求格式。" }, statusCode: 200);
    }
    var endpoint = body.Endpoint.Trim().TrimEnd('/');
    var modelId = (body.Provider == "Azure" && !string.IsNullOrWhiteSpace(body.DeploymentName))
        ? body.DeploymentName.Trim()
        : body.ModelId.Trim();
    var apiKey = body.ApiKey?.Trim() ?? "";
    var url = endpoint.Contains("/v1") ? endpoint + "/chat/completions" : endpoint + "/v1/chat/completions";
    if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != "http" && uri.Scheme != "https"))
        return Results.Json(new { ok = false, message = "接口地址格式无效，请填写有效的 http(s) 地址。" }, statusCode: 200);
    using var http = new HttpClient();
    http.Timeout = TimeSpan.FromSeconds(5);
    var payload = new
    {
        model = modelId,
        messages = new[] { new { role = "user", content = "Hi" } },
        max_tokens = 2
    };
    var request = new HttpRequestMessage(HttpMethod.Post, uri);
    if (!string.IsNullOrEmpty(apiKey))
        request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + apiKey);
    request.Content = new StringContent(JsonSerializer.Serialize(payload), System.Text.Encoding.UTF8, "application/json");
    try
    {
        var response = await http.SendAsync(request);
        var responseText = await response.Content.ReadAsStringAsync();
        if (response.IsSuccessStatusCode)
            return Results.Ok(new { ok = true, message = "连接成功，接口与 Key/模型可用。" });
        logger.LogWarning("Test AI failed: {Status} {Body}", response.StatusCode, responseText.Length > 200 ? responseText[..200] + "..." : responseText);
        var err = responseText.Length > 300 ? responseText[..300] + "..." : responseText;
        return Results.Json(new { ok = false, message = "请求失败: " + (int)response.StatusCode + " " + response.ReasonPhrase + (string.IsNullOrEmpty(err) ? "" : " — " + err) }, statusCode: 502);
    }
    catch (TaskCanceledException)
    {
        return Results.Json(new { ok = false, message = "连接超时，请检查接口地址或网络。" }, statusCode: 504);
    }
    catch (Exception ex)
    {
        logger.LogWarning(ex, "Test AI exception");
        return Results.Json(new { ok = false, message = "连接失败: " + ex.Message }, statusCode: 502);
    }
});

app.MapPost("/api/config/test-embedding", async (HttpContext ctx, ILogger<Program> logger) =>
{
    TestEmbeddingRequest? body;
    try
    {
        body = await JsonSerializer.DeserializeAsync<TestEmbeddingRequest>(ctx.Request.Body, JsonCtx.Default.TestEmbeddingRequest);
    }
    catch (Exception ex)
    {
        logger.LogWarning(ex, "Test Embedding: request body deserialize failed");
        return Results.Json(new { ok = false, message = "请求体解析失败，请确认发送的是 JSON，字段为 endpoint、modelId、apiKey（小写驼峰）。" }, statusCode: 400);
    }
    if (body == null || string.IsNullOrWhiteSpace(body.Endpoint) || string.IsNullOrWhiteSpace(body.ModelId))
    {
        return Results.Json(new { ok = false, message = "请求参数无效：缺少或为空 endpoint 或 modelId。" }, statusCode: 400);
    }
    var endpoint = body.Endpoint.Trim().TrimEnd('/');
    var modelId = body.ModelId.Trim();
    var apiKey = body.ApiKey?.Trim() ?? "";
    var path = endpoint.Contains("/v1", StringComparison.OrdinalIgnoreCase) ? "/embeddings" : "/v1/embeddings";
    var url = endpoint + path;
    if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != "http" && uri.Scheme != "https"))
    {
        return Results.Json(new { ok = false, message = "接口地址格式无效，请填写有效的 http(s) 地址。" }, statusCode: 400);
    }
    using var http = new HttpClient();
    http.Timeout = TimeSpan.FromSeconds(5);
    var payload = new { model = modelId, input = "test" };
    var request = new HttpRequestMessage(HttpMethod.Post, uri);
    if (!string.IsNullOrEmpty(apiKey))
        request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + apiKey);
    request.Content = new StringContent(JsonSerializer.Serialize(payload), System.Text.Encoding.UTF8, "application/json");
    try
    {
        var response = await http.SendAsync(request);
        var responseText = await response.Content.ReadAsStringAsync();
        if (response.IsSuccessStatusCode)
            return Results.Ok(new { ok = true, message = "连接成功，Embedding 接口可用。" });
        logger.LogWarning("Test Embedding failed: {Status} {Body}", response.StatusCode, responseText.Length > 200 ? responseText[..200] + "..." : responseText);
        var err = responseText.Length > 300 ? responseText[..300] + "..." : responseText;
        return Results.Json(new { ok = false, message = "请求失败: " + (int)response.StatusCode + " " + response.ReasonPhrase + (string.IsNullOrEmpty(err) ? "" : " — " + err) }, statusCode: 502);
    }
    catch (TaskCanceledException)
    {
        return Results.Json(new { ok = false, message = "连接超时，请检查接口地址或网络。" }, statusCode: 504);
    }
    catch (Exception ex)
    {
        logger.LogWarning(ex, "Test Embedding exception");
        return Results.Json(new { ok = false, message = "连接失败: " + ex.Message }, statusCode: 502);
    }
});

app.MapPost("/api/config/test-stt", async (HttpContext ctx, ILogger<Program> logger, IHttpClientFactory httpClientFactory) =>
{
    TestSttRequest? body;
    try
    {
        body = await JsonSerializer.DeserializeAsync<TestSttRequest>(ctx.Request.Body, JsonCtx.Default.TestSttRequest);
    }
    catch (Exception ex)
    {
        logger.LogWarning(ex, "Test STT: request body deserialize failed");
        return Results.Json(new { ok = false, message = "请求体解析失败，请确认发送的是 JSON，字段为 endpoint、modelId、apiKey（小写驼峰）。" }, statusCode: 400);
    }
    if (body == null || string.IsNullOrWhiteSpace(body.Endpoint))
    {
        return Results.Json(new { ok = false, message = "请求参数无效：缺少或为空 endpoint。" }, statusCode: 400);
    }

    var endpoint = body.Endpoint.Trim().TrimEnd('/');
    var modelId = string.IsNullOrWhiteSpace(body.ModelId) ? "whisper-1" : body.ModelId.Trim();
    var apiKey = body.ApiKey?.Trim() ?? "";
    if (string.IsNullOrEmpty(apiKey))
    {
        return Results.Json(new { ok = false, message = "请求参数无效：缺少 apiKey。" }, statusCode: 400);
    }

    if (!ModelConnectionKind.IsValidStt(body.ConnectionKind))
    {
        return Results.Json(new { ok = false, message = "请求参数无效：connectionKind 取值无效，请留空或填写 openai_whisper_multipart、dashscope_openai_chat_audio。" }, statusCode: 400);
    }

    SttUpstreamAdapter.UpstreamKind kind;
    try
    {
        kind = SttTranscriberResolver.Resolve(endpoint, body.ConnectionKind, body.VendorId);
    }
    catch (InvalidOperationException ex)
    {
        return Results.Json(new { ok = false, message = ex.Message }, statusCode: 400);
    }

    // 最小 WAV 头（44 字节）+ 无采样，用于测试连接
    var minimalWav = SttUpstreamAdapter.BuildMinimalWavPcm16kMono(durationMs: 200);

    using var http = httpClientFactory.CreateClient("STT");
    http.Timeout = TimeSpan.FromSeconds(15);

    try
    {
        if (kind == SttUpstreamAdapter.UpstreamKind.DashScopeQwenOpenAICompatible)
        {
            try
            {
                SttUpstreamAdapter.ValidateDashScopeModelIdPresent(modelId);
            }
            catch (InvalidOperationException ex)
            {
                return Results.Json(new { ok = false, message = ex.Message }, statusCode: 400);
            }

            var urlDash = SttUpstreamAdapter.BuildDashScopeChatCompletionsUrl(endpoint);
            if (!Uri.TryCreate(urlDash, UriKind.Absolute, out var uriDash) || (uriDash.Scheme != "http" && uriDash.Scheme != "https"))
            {
                return Results.Json(new { ok = false, message = "接口地址格式无效，请填写有效的 http(s) 地址。" }, statusCode: 400);
            }

            var dataUrl = SttUpstreamAdapter.BuildAudioDataUrl(minimalWav, "audio/wav");
            var jsonPayload = SttUpstreamAdapter.BuildDashScopeOpenAICompatibleRequestJson(modelId, dataUrl, language: null);
            var requestDash = new HttpRequestMessage(HttpMethod.Post, uriDash);
            requestDash.Headers.TryAddWithoutValidation("Authorization", "Bearer " + apiKey);
            requestDash.Content = new StringContent(jsonPayload, System.Text.Encoding.UTF8, "application/json");

            var response = await http.SendAsync(requestDash);
            var responseText = await response.Content.ReadAsStringAsync();
            if (response.IsSuccessStatusCode)
                return Results.Ok(new { ok = true, message = "连接成功，STT（DashScope Qwen/OpenAI 兼容）接口可用。" });
            if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
                return Results.Json(new { ok = false, message = "API Key 无效或未授权。" }, statusCode: 401);

            logger.LogWarning("Test STT failed: {Status} {Body}", response.StatusCode, responseText.Length > 200 ? responseText[..200] + "..." : responseText);
            var err = responseText.Length > 300 ? responseText[..300] + "..." : responseText;
            return Results.Json(new { ok = false, message = "请求失败: " + (int)response.StatusCode + " " + (response.ReasonPhrase ?? "") + (string.IsNullOrEmpty(err) ? "" : " — " + err) }, statusCode: 502);
        }

        // Whisper 兼容
        var urlWhisper = SttUpstreamAdapter.BuildWhisperTranscriptionsUrl(endpoint);
        if (!Uri.TryCreate(urlWhisper, UriKind.Absolute, out var uriW) || (uriW.Scheme != "http" && uriW.Scheme != "https"))
        {
            return Results.Json(new { ok = false, message = "接口地址格式无效，请填写有效的 http(s) 地址。" }, statusCode: 400);
        }

        using var content = new MultipartFormDataContent();
        var fileContent = new StreamContent(new MemoryStream(minimalWav));
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
        content.Add(fileContent, "file", "test.wav");
        content.Add(new StringContent(modelId), "model");

        var requestWhisper = new HttpRequestMessage(HttpMethod.Post, uriW);
        requestWhisper.Headers.TryAddWithoutValidation("Authorization", "Bearer " + apiKey);
        requestWhisper.Content = content;

        var responseW = await http.SendAsync(requestWhisper);
        var responseTextW = await responseW.Content.ReadAsStringAsync();
        if (responseW.IsSuccessStatusCode)
            return Results.Ok(new { ok = true, message = "连接成功，STT（Whisper 兼容）接口可用。" });
        if (responseW.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            return Results.Json(new { ok = false, message = "API Key 无效或未授权。" }, statusCode: 401);

        logger.LogWarning("Test STT failed: {Status} {Body}", responseW.StatusCode, responseTextW.Length > 200 ? responseTextW[..200] + "..." : responseTextW);
        var errW = responseTextW.Length > 300 ? responseTextW[..300] + "..." : responseTextW;
        return Results.Json(new { ok = false, message = "请求失败: " + (int)responseW.StatusCode + " " + (responseW.ReasonPhrase ?? "") + (string.IsNullOrEmpty(errW) ? "" : " — " + errW) }, statusCode: 502);
    }
    catch (TaskCanceledException)
    {
        return Results.Json(new { ok = false, message = "连接超时，请检查接口地址或网络。" }, statusCode: 504);
    }
    catch (Exception ex)
    {
        logger.LogWarning(ex, "Test STT exception");
        return Results.Json(new { ok = false, message = "连接失败: " + ex.Message }, statusCode: 502);
    }
});

app.MapPost("/api/config/test-ocr", async (HttpContext ctx, ILogger<Program> logger) =>
{
    TestOcrRequest? body;
    try
    {
        body = await JsonSerializer.DeserializeAsync<TestOcrRequest>(ctx.Request.Body, JsonCtx.Default.TestOcrRequest);
    }
    catch (Exception ex)
    {
        logger.LogWarning(ex, "Test OCR: request body deserialize failed");
        return Results.Json(new { ok = false, message = "请求体解析失败，请确认发送的是 JSON，字段为 endpoint、apiKey（可选 language，均为小写驼峰）。" }, statusCode: 400);
    }
    if (body == null || string.IsNullOrWhiteSpace(body.Endpoint))
    {
        return Results.Json(new { ok = false, message = "请求参数无效：缺少或为空 endpoint。" }, statusCode: 400);
    }
    var endpoint = body.Endpoint.Trim().TrimEnd('/');
    var apiKey = body.ApiKey?.Trim() ?? "";
    if (string.IsNullOrEmpty(apiKey))
    {
        return Results.Json(new { ok = false, message = "请求参数无效：缺少 apiKey。" }, statusCode: 400);
    }
    if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) || (uri.Scheme != "http" && uri.Scheme != "https"))
    {
        return Results.Json(new { ok = false, message = "接口地址格式无效，请填写有效的 http(s) 地址。" }, statusCode: 400);
    }

    if (!ModelConnectionKind.IsValidOcr(body.ConnectionKind))
    {
        return Results.Json(new { ok = false, message = "请求参数无效：connectionKind 取值无效，请留空或填写 openai_compatible_multipart、dashscope_openai_chat_image。" }, statusCode: 400);
    }

    var ocrProbe = new OcrModelEntry
    {
        Endpoint = endpoint,
        ConnectionKind = (body.ConnectionKind ?? "").Trim(),
        VendorId = (body.VendorId ?? "").Trim(),
        ModelId = string.IsNullOrWhiteSpace(body.ModelId) ? "" : body.ModelId!.Trim(),
        Language = body.Language
    };
    OcrBackendKind ocrBackend;
    try
    {
        ocrBackend = OcrExtractorResolver.Resolve(ocrProbe);
    }
    catch (InvalidOperationException ex)
    {
        return Results.Json(new { ok = false, message = ex.Message }, statusCode: 400);
    }

    // 最小 10x10 PNG，用于测试连接
    var minimalPng = new byte[]
    {
        0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a, 0x00, 0x00, 0x00, 0x0d, 0x49, 0x48, 0x44, 0x52,
        0x00, 0x00, 0x00, 0x0a, 0x00, 0x00, 0x00, 0x0a, 0x08, 0x06, 0x00, 0x00, 0x00, 0x8d, 0x32, 0xcf, 0xbd,
        0x00, 0x00, 0x00, 0x01, 0x73, 0x52, 0x47, 0x42, 0x00, 0xae, 0xce, 0x1c, 0xe9, 0x00, 0x00, 0x00,
        0x04, 0x67, 0x41, 0x4d, 0x41, 0x00, 0x00, 0xb1, 0x8f, 0x0b, 0xfc, 0x61, 0x05, 0x00, 0x00, 0x00, 0x09,
        0x70, 0x48, 0x59, 0x73, 0x00, 0x00, 0x0e, 0xc3, 0x00, 0x00, 0x0e, 0xc3, 0x01, 0xc7, 0x6f, 0xa8, 0x64,
        0x00, 0x00, 0x00, 0x17, 0x49, 0x44, 0x41, 0x54, 0x28, 0x53, 0x63, 0xf8, 0x4f, 0x24, 0x60, 0x40,
        0x17, 0xc0, 0x05, 0x46, 0x15, 0xe2, 0x05, 0x44, 0x2b, 0x04, 0x00, 0x23, 0xfb, 0x8e, 0x80, 0x69,
        0x85, 0x5d, 0x2c, 0x00, 0x00, 0x00, 0x00, 0x49, 0x45, 0x4e, 0x44, 0xae, 0x42, 0x60, 0x82
    };

    if (ocrBackend == OcrBackendKind.DashScopeOpenAiChatImage)
    {
        var dataUrl = OcrUpstreamAdapter.BuildDataUrlFromImageBytes(minimalPng, "image/png");
        var defaultModelId = "qwen-vl-ocr-latest";
        var prompt = "请只输出图片中的识别文字，不要输出解释或额外格式。";
        var configuredModel = string.IsNullOrWhiteSpace(body.ModelId) ? null : body.ModelId.Trim();
        var requestJson = OcrUpstreamAdapter.BuildDashScopeOpenAICompatibleOcrRequestJson(
            defaultModelId,
            dataUrl,
            prompt,
            body.Language,
            configuredModel);

        var chatUrl = OcrUpstreamAdapter.BuildDashScopeChatCompletionsUrl(endpoint);

        using var httpClientDash = new HttpClient();
        httpClientDash.Timeout = TimeSpan.FromSeconds(15);
        var requestDash = new HttpRequestMessage(HttpMethod.Post, chatUrl);
        requestDash.Headers.TryAddWithoutValidation("Authorization", "Bearer " + apiKey);
        requestDash.Content = new StringContent(requestJson, Encoding.UTF8, "application/json");

        try
        {
            var response = await httpClientDash.SendAsync(requestDash);
            var responseText = await response.Content.ReadAsStringAsync();
            if (response.IsSuccessStatusCode)
                return Results.Ok(new { ok = true, message = "连接成功，OCR 接口可用。" });
            if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
                return Results.Json(new { ok = false, message = "API Key 无效或未授权。" }, statusCode: 401);

            logger.LogWarning("Test OCR failed: {Status} {Body}", response.StatusCode,
                responseText.Length > 200 ? responseText[..200] + "..." : responseText);
            var err = responseText.Length > 300 ? responseText[..300] + "..." : responseText;
            return Results.Json(new { ok = false, message = "请求失败: " + (int)response.StatusCode + " " + (response.ReasonPhrase ?? "") + (string.IsNullOrEmpty(err) ? "" : " — " + err) }, statusCode: 502);
        }
        catch (TaskCanceledException)
        {
            return Results.Json(new { ok = false, message = "连接超时，请检查接口地址或网络。" }, statusCode: 504);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Test OCR exception");
            return Results.Json(new { ok = false, message = "连接失败: " + ex.Message }, statusCode: 502);
        }
    }

    using var content = new MultipartFormDataContent();
    var fileContent = new StreamContent(new MemoryStream(minimalPng));
    fileContent.Headers.ContentType = new MediaTypeHeaderValue("image/png");
    content.Add(fileContent, "file", "test.png");
    using var httpClient = new HttpClient();
    httpClient.Timeout = TimeSpan.FromSeconds(15);
    var request = new HttpRequestMessage(HttpMethod.Post, uri);
    request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + apiKey);
    request.Content = content;
    try
    {
        var response = await httpClient.SendAsync(request);
        var responseText = await response.Content.ReadAsStringAsync();
        if (response.IsSuccessStatusCode)
            return Results.Ok(new { ok = true, message = "连接成功，OCR 接口可用。" });
        if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            return Results.Json(new { ok = false, message = "API Key 无效或未授权。" }, statusCode: 401);
        logger.LogWarning("Test OCR failed: {Status} {Body}", response.StatusCode, responseText.Length > 200 ? responseText[..200] + "..." : responseText);
        var err = responseText.Length > 300 ? responseText[..300] + "..." : responseText;
        return Results.Json(new { ok = false, message = "请求失败: " + (int)response.StatusCode + " " + (response.ReasonPhrase ?? "") + (string.IsNullOrEmpty(err) ? "" : " — " + err) }, statusCode: 502);
    }
    catch (TaskCanceledException)
    {
        return Results.Json(new { ok = false, message = "连接超时，请检查接口地址或网络。" }, statusCode: 504);
    }
    catch (Exception ex)
    {
        logger.LogWarning(ex, "Test OCR exception");
        return Results.Json(new { ok = false, message = "连接失败: " + ex.Message }, statusCode: 502);
    }
});

app.MapGet("/api/tools/builtin", () =>
{
    var builtIn = new List<BuiltInPluginInfo>
    {
        new() { Id = "Browser", Name = "Browser", Description = "网页高亮、截图、运行页面脚本、整页截图等" },
        new() { Id = "File", Name = "File", Description = "附件路径解析、文件大小查询、保存截图到下载文件夹" },
        new() { Id = "System", Name = "System", Description = "当前时间等系统信息，用于回答用户关于日期、时间的问题" },
        new() { Id = "MCP_STT", Name = "MCP_STT", Description = "内置语音转文字（Whisper）：将音频文件转成文字，供整理成文档等" },
        new() { Id = "MCP_OCR", Name = "MCP_OCR", Description = "内置 OCR：从图片中提取文字，供整理成文档等（需在模型设置中配置 OCR）" },
        new() { Id = "CLI", Name = "CLI", Description = "执行白名单内系统命令" },
        new() { Id = "Excel", Name = "Excel", Description = "读写 Excel 文档" },
        new() { Id = "Word", Name = "Word", Description = "读写 Word 文档" },
        new() { Id = "Ppt", Name = "Ppt", Description = "读写 PPT 演示文稿" },
        new() { Id = "CurrentDocument", Name = "CurrentDocument", Description = "当前打开的 Word/Excel/PPT 文档（任务窗格连接时）：正文/选区/表格/查找替换、Excel 区域/公式/工作表、PPT 幻灯片、预定义脚本" },
        new() { Id = "Tavily", Name = "Tavily", Description = "Tavily 网页搜索与 URL 正文提取（需配置 TAVILY_API_KEY）" },
        new() { Id = "ClawhubSkill", Name = "ClawhubSkill", Description = "运行 Clawhub 可执行技能中的 node 脚本（无原生适配器时使用）" },
        new() { Id = "Memory", Name = "Memory", Description = "长期记忆：用户可点名「记住」；也可主动保存/检索习惯、取向与关键事实（需配置 Embedding）" },
        new() { Id = "AccurateData", Name = "AccurateData", Description = "准确数据：用户可点名按 id 存取；复杂任务中可主动落盘大块结构化中间结果以减上下文" },
        new() { Id = "Plan", Name = "Plan", Description = "计划：用户可点名列计划；复杂多步任务可主动生成/按步执行已保存的实现计划" },
        new() { Id = "UserOptions", Name = "UserOptions", Description = "候选项确认（ask_options）：侧栏分步单选让用户确认方案/格式等；需在 Chrome 扩展侧栏连接（WPS 等端若未接 UI 则可能超时）" },
        new() { Id = "SkillAuthor", Name = "SkillAuthor", Description = "技能撰写：根据目标与对话摘要生成 SKILL.md 并保存为用户技能，与设置页技能列表一致" }
    };
    return Results.Json(builtIn, JsonCtx.Default.ListBuiltInPluginInfo);
});
app.MapGet("/api/config/default-allowed-cli", () => Results.Json(CliScriptEndKeys.DefaultAllowedCommands));
app.MapGet("/api/config/default-allowed-page-scripts", () => Results.Json(CliScriptEndKeys.DefaultAllowedScriptIds));
app.MapPost("/api/transcribe", async (HttpContext ctx, ITranscribeService transcribeService, ILogger<Program> logger) =>
{
    if (!ctx.Request.HasFormContentType)
    {
        return Results.Json(new { ok = false, message = "请使用 multipart/form-data 上传音频文件，表单字段名为 file。" }, statusCode: 400);
    }
    var form = await ctx.Request.ReadFormAsync();
    var file = form.Files.GetFile("file");
    if (file == null || file.Length == 0)
    {
        return Results.Json(new { ok = false, message = "未收到音频文件或文件为空，请选择文件后上传（表单字段 file）。" }, statusCode: 400);
    }
    const long whisperLimit = 25 * 1024 * 1024; // 25 MB
    if (file.Length > whisperLimit)
    {
        return Results.Json(new { ok = false, message = "单文件超过 25MB 限制，请使用更短的音频或先分片后再试。" }, statusCode: 413);
    }
    try
    {
        await using var stream = file.OpenReadStream();
        var text = await transcribeService.TranscribeAsync(stream, file.ContentType, null, ctx.RequestAborted);
        return Results.Json(new { ok = true, text });
    }
    catch (InvalidOperationException ex)
    {
        return Results.Json(new { ok = false, message = ex.Message }, statusCode: ex.Message.Contains("未配置") ? 400 : 502);
    }
    catch (Exception ex)
    {
        logger.LogWarning(ex, "Transcribe exception");
        return Results.Json(new { ok = false, message = "语音转写失败: " + ex.Message }, statusCode: 502);
    }
});
app.MapGet("/api/skills", (SkillService skills) => Results.Json(skills.GetAllSkills(), JsonCtx.Default.ListSkillDefinition));
app.MapPost("/api/skills", async (HttpContext ctx, SkillService skills) =>
{
    var newSkill = await JsonSerializer.DeserializeAsync<SkillDefinition>(ctx.Request.Body, JsonCtx.Default.SkillDefinition);
    if (newSkill != null)
    {
        skills.SaveSkill(newSkill);
        return Results.Ok(newSkill);
    }
    return Results.Json(new { ok = false, message = "请求体解析失败或技能数据无效，请确认 JSON 格式与必填字段。" }, statusCode: 400);
});
app.MapDelete("/api/skills/{id}", (string id, SkillService skills) => 
{
    skills.DeleteSkill(id);
    return Results.Ok();
});

// ----- 阶段 3：RAG 摄入与记忆 CRUD -----
app.MapPost("/api/rag/ingest", async (HttpContext ctx, IMemoryStoreService memory) =>
{
    if (!memory.IsAvailable)
        return Results.Json(new { ok = false, message = "未配置 Embedding 模型，无法摄入知识库。" }, statusCode: 400);
    var body = await JsonSerializer.DeserializeAsync<RagIngestRequest>(ctx.Request.Body, JsonCtx.Default.RagIngestRequest);
    if (body == null || string.IsNullOrWhiteSpace(body.KnowledgeBaseId) || string.IsNullOrWhiteSpace(body.Text))
        return Results.BadRequest(new { ok = false, message = "需要 knowledgeBaseId 和 text。" });
    var chunks = TextChunker.Chunk(body.Text, body.MaxChunkChars, body.OverlapChars);
    var added = 0;
    for (var i = 0; i < chunks.Count; i++)
    {
        var chunkId = $"{body.KnowledgeBaseId}:{i}";
        await memory.AddChunkToKnowledgeBaseAsync(body.KnowledgeBaseId, chunkId, chunks[i], null).ConfigureAwait(false);
        added++;
    }
    return Results.Json(new { ok = true, chunksAdded = added });
});

app.MapGet("/api/memory", async (string? sessionId, string? scope, string? agentName, int skip, int take, IMemoryStoreService memory) =>
{
    // 列表不依赖 Embedding，未配置时也可返回已存在的记忆
    var filterSessionId = (scope ?? "").Trim().ToLowerInvariant() == "shared"
        ? OfficeCopilot.Server.Services.Memory.MemoryScopes.SharedSessionId
        : (scope == "all" ? null : sessionId);
    var agentNameFilter = string.IsNullOrWhiteSpace(agentName) ? null : agentName.Trim();
    var list = await memory.ListAsync(filterSessionId, Math.Max(0, skip), Math.Clamp(take, 1, 100), agentNameFilter).ConfigureAwait(false);
    return Results.Json(new { ok = true, items = list });
});

app.MapPost("/api/memory", async (HttpContext ctx, IMemoryStoreService memory) =>
{
    if (!memory.IsAvailable)
        return Results.Json(new { ok = false, message = "未配置 Embedding 模型。" }, statusCode: 400);
    var body = await JsonSerializer.DeserializeAsync<MemoryAddRequest>(ctx.Request.Body, JsonCtx.Default.MemoryAddRequest);
    if (body == null || string.IsNullOrWhiteSpace(body.Text))
        return Results.BadRequest(new { ok = false, message = "需要 text。" });
    var metadata = string.IsNullOrWhiteSpace(body.Tags) ? null : new Dictionary<string, string> { ["tags"] = body.Tags };
    var id = await memory.SaveAsync(null, body.Text.Trim(), body.ScopeShared ? null : body.SessionId, metadata, body.ScopeShared).ConfigureAwait(false);
    return Results.Json(new { ok = true, id });
});

app.MapGet("/api/memory/{id}", async (string id, IMemoryStoreService memory) =>
{
    var item = await memory.GetAsync(id).ConfigureAwait(false);
    if (item == null) return Results.Json(new { ok = false, message = "未找到该记忆。" }, statusCode: 404);
    return Results.Json(item);
});

app.MapPut("/api/memory/{id}", async (string id, HttpContext ctx, IMemoryStoreService memory) =>
{
    if (!memory.IsAvailable)
        return Results.Json(new { ok = false, message = "未配置 Embedding 模型。" }, statusCode: 400);
    var body = await JsonSerializer.DeserializeAsync<MemoryUpdateRequest>(ctx.Request.Body, JsonCtx.Default.MemoryUpdateRequest);
    if (body == null || string.IsNullOrWhiteSpace(body.Text))
        return Results.BadRequest(new { ok = false, message = "需要 text。" });
    var existing = await memory.GetAsync(id).ConfigureAwait(false);
    var sessionId = existing?.SessionId;
    var isShared = body.ScopeShared.HasValue
        ? body.ScopeShared.Value
        : string.Equals(sessionId, OfficeCopilot.Server.Services.Memory.MemoryScopes.SharedSessionId, StringComparison.Ordinal);
    if (body.ScopeShared == false && string.Equals(sessionId, OfficeCopilot.Server.Services.Memory.MemoryScopes.SharedSessionId, StringComparison.Ordinal))
        sessionId = null;
    var metadata = string.IsNullOrWhiteSpace(body.Tags) ? null : new Dictionary<string, string> { ["tags"] = body.Tags };
    await memory.SaveAsync(id, body.Text.Trim(), sessionId, metadata, isShared).ConfigureAwait(false);
    return Results.Ok(new { ok = true });
});

app.MapDelete("/api/memory/{id}", async (string id, IMemoryStoreService memory) =>
{
    var deleted = await memory.DeleteAsync(id).ConfigureAwait(false);
    if (!deleted) return Results.Json(new { ok = false, message = "未找到该记忆或已删除。" }, statusCode: 404);
    return Results.Ok(new { ok = true });
});

app.MapGet("/api/plans", async (string? agentName, IPlanStore planStore, CancellationToken ct) =>
{
    var list = await planStore.ListAsync(ct).ConfigureAwait(false);
    if (!string.IsNullOrWhiteSpace(agentName))
    {
        var name = agentName.Trim();
        list = list.Where(m => string.Equals(m.CreatedByDisplayName, name, StringComparison.OrdinalIgnoreCase)).ToList();
    }
    return Results.Json(list, JsonCtx.Default.ListPlanMeta);
});

app.MapGet("/api/plans/{id}", async (string id, IPlanStore planStore, CancellationToken ct) =>
{
    var result = await planStore.GetAsync(id, ct).ConfigureAwait(false);
    if (result == null) return Results.Json(new { ok = false, message = "未找到该计划。" }, statusCode: 404);
    return Results.Json(new { content = result.Value.Content, meta = result.Value.Meta });
});

app.MapPut("/api/plans/{id}", async (string id, HttpContext ctx, IPlanStore planStore, CancellationToken ct) =>
{
    var body = await JsonSerializer.DeserializeAsync<PlanUpdateRequest>(ctx.Request.Body, JsonCtx.Default.PlanUpdateRequest);
    if (body == null) return Results.BadRequest(new { ok = false, message = "需要 content。" });
    var existing = await planStore.GetAsync(id, ct).ConfigureAwait(false);
    if (existing == null) return Results.Json(new { ok = false, message = "未找到该计划。" }, statusCode: 404);
    var meta = existing.Value.Meta;
    if (!string.IsNullOrWhiteSpace(body.Title)) meta.Title = body.Title;
    if (!string.IsNullOrWhiteSpace(body.Status)) meta.Status = body.Status;
    meta.UpdatedAt = DateTimeOffset.UtcNow;
    await planStore.SaveAsync(id, body.Content ?? "", meta, ct).ConfigureAwait(false);
    return Results.Ok(new { ok = true });
});

app.MapDelete("/api/plans/{id}", async (string id, IPlanStore planStore, CancellationToken ct) =>
{
    var deleted = await planStore.DeleteAsync(id, ct).ConfigureAwait(false);
    if (!deleted) return Results.Json(new { ok = false, message = "未找到该计划或已删除。" }, statusCode: 404);
    return Results.Ok(new { ok = true });
});

// ----- 准确数据 API（仅列表 + 删除，与 MCP 共用目录）-----
static string GetAccurateDataDirectory(ConfigService configService)
{
    var dir = (configService.Current.AccurateDataDirectory ?? "").Trim();
    if (string.IsNullOrEmpty(dir))
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        dir = Path.Combine(appData, "OfficeCopilot", "AccurateData");
    }
    else
    {
        dir = Environment.ExpandEnvironmentVariables(dir);
        if (!Path.IsPathRooted(dir))
            dir = Path.Combine(AppContext.BaseDirectory, dir);
    }
    return Path.GetFullPath(dir);
}

static string SanitizeAccurateDataId(string? id)
{
    if (string.IsNullOrWhiteSpace(id)) return "";
    return System.Text.RegularExpressions.Regex.Replace(id.Trim(), @"[^\w\-]", "_");
}

app.MapGet("/api/accurate-data", (ConfigService configService) =>
{
    var root = GetAccurateDataDirectory(configService);
    if (!Directory.Exists(root)) return Results.Json(Array.Empty<object>());
    var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    foreach (var file in Directory.EnumerateFiles(root, "*.*"))
    {
        var name = Path.GetFileNameWithoutExtension(file);
        var ext = Path.GetExtension(file);
        if (ext.Equals(".meta.json", StringComparison.OrdinalIgnoreCase)) continue;
        if (!ext.Equals(".md", StringComparison.OrdinalIgnoreCase) && !ext.Equals(".json", StringComparison.OrdinalIgnoreCase)) continue;
        ids.Add(name);
    }
    var list = ids.Select(id => new { id, format = File.Exists(Path.Combine(root, id + ".md")) ? "md" : "json" }).OrderBy(x => x.id).ToList();
    return Results.Json(list);
});

app.MapDelete("/api/accurate-data/{id}", (string id, ConfigService configService) =>
{
    var safeId = SanitizeAccurateDataId(id);
    if (string.IsNullOrEmpty(safeId)) return Results.Json(new { ok = false, message = "id 无效或包含非法字符。" }, statusCode: 400);
    var root = GetAccurateDataDirectory(configService);
    Directory.CreateDirectory(root);
    var rootFull = Path.GetFullPath(root);
    var deleted = false;
    foreach (var name in new[] { safeId + ".md", safeId + ".json", safeId + ".meta.json" })
    {
        var path = Path.Combine(root, name);
        if (!File.Exists(path)) continue;
        var full = Path.GetFullPath(path);
        if (!full.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase)) continue;
        File.Delete(path);
        deleted = true;
    }
    return deleted ? Results.Ok(new { ok = true }) : Results.Json(new { ok = false, message = "未找到该准确数据或已删除。" }, statusCode: 404);
});

// ----- 定时任务 API -----
app.MapGet("/api/scheduled-tasks", async (IScheduledTaskStore taskStore, CancellationToken ct) =>
{
    var list = await taskStore.ListAsync(ct).ConfigureAwait(false);
    return Results.Json(list, JsonCtx.Default.ListScheduledTaskMeta);
});

app.MapGet("/api/scheduled-tasks/{id}", async (string id, IScheduledTaskStore taskStore, CancellationToken ct) =>
{
    var result = await taskStore.GetAsync(id, ct).ConfigureAwait(false);
    if (result == null) return Results.Json(new { ok = false, message = "未找到该定时任务。" }, statusCode: 404);
    return Results.Json(new { content = result.Value.Content, meta = result.Value.Meta });
});

app.MapPost("/api/scheduled-tasks", async (HttpContext ctx, IScheduledTaskStore taskStore, CancellationToken ct) =>
{
    var body = await JsonSerializer.DeserializeAsync<ScheduledTaskCreateRequest>(ctx.Request.Body, JsonCtx.Default.ScheduledTaskCreateRequest);
    if (body == null || string.IsNullOrWhiteSpace(body.Title) || string.IsNullOrWhiteSpace(body.Content))
        return Results.BadRequest(new { ok = false, message = "需要 title 和 content。" });
    var meta = new ScheduledTaskMeta
    {
        Title = body.Title.Trim(),
        ScheduleType = (body.ScheduleType ?? "cron").Trim(),
        CronExpression = body.CronExpression?.Trim(),
        IntervalMinutes = body.IntervalMinutes,
        Enabled = true,
        TimeZone = body.TimeZone?.Trim(),
        EndAt = body.EndAt,
        MaxRuns = body.MaxRuns,
        DeleteAfterRun = body.DeleteAfterRun
    };
    var nextRun = CronNextRun.GetNextRunAt(meta, DateTimeOffset.UtcNow);
    meta.NextRunAt = nextRun;
    var id = await taskStore.SaveAsync(null, body.Content.Trim(), meta, ct).ConfigureAwait(false);
    return Results.Json(new { ok = true, id });
});

app.MapPut("/api/scheduled-tasks/{id}", async (string id, HttpContext ctx, IScheduledTaskStore taskStore, CancellationToken ct) =>
{
    var body = await JsonSerializer.DeserializeAsync<ScheduledTaskUpdateRequest>(ctx.Request.Body, JsonCtx.Default.ScheduledTaskUpdateRequest);
    if (body == null) return Results.BadRequest(new { ok = false, message = "需要请求体。" });
    var existing = await taskStore.GetAsync(id, ct).ConfigureAwait(false);
    if (existing == null) return Results.Json(new { ok = false, message = "未找到该定时任务。" }, statusCode: 404);
    var meta = existing.Value.Meta;
    var content = body.Content ?? existing.Value.Content;
    if (!string.IsNullOrWhiteSpace(body.Title)) meta.Title = body.Title.Trim();
    if (body.Enabled.HasValue) meta.Enabled = body.Enabled.Value;
    if (body.ScheduleType != null) meta.ScheduleType = body.ScheduleType.Trim();
    if (body.CronExpression != null) meta.CronExpression = string.IsNullOrWhiteSpace(body.CronExpression) ? null : body.CronExpression.Trim();
    if (body.IntervalMinutes.HasValue) meta.IntervalMinutes = body.IntervalMinutes;
    if (body.TimeZone != null) meta.TimeZone = string.IsNullOrWhiteSpace(body.TimeZone) ? null : body.TimeZone.Trim();
    if (body.EndAt.HasValue) meta.EndAt = body.EndAt;
    if (body.MaxRuns.HasValue) meta.MaxRuns = body.MaxRuns;
    if (body.DeleteAfterRun.HasValue) meta.DeleteAfterRun = body.DeleteAfterRun.Value;
    meta.NextRunAt = CronNextRun.GetNextRunAt(meta, DateTimeOffset.UtcNow);
    await taskStore.SaveAsync(id, content, meta, ct).ConfigureAwait(false);
    return Results.Ok(new { ok = true });
});

app.MapDelete("/api/scheduled-tasks/{id}", async (string id, IScheduledTaskStore taskStore, CancellationToken ct) =>
{
    var deleted = await taskStore.DeleteAsync(id, ct).ConfigureAwait(false);
    if (!deleted) return Results.Json(new { ok = false, message = "未找到该定时任务或已删除。" }, statusCode: 404);
    return Results.Ok(new { ok = true });
});

{
    var configSvc = app.Services.GetRequiredService<ConfigService>();
    var sessionMgr = app.Services.GetRequiredService<SessionManager>();
    var themeBroadcastLogger = app.Services.GetRequiredService<Microsoft.Extensions.Logging.ILoggerFactory>().CreateLogger("UiThemeBroadcast");
    string? lastBroadcastUiTheme = null;
    configSvc.OnConfigChanged += () =>
    {
        var id = string.IsNullOrWhiteSpace(configSvc.Current.UiThemeId)
            ? "dark"
            : configSvc.Current.UiThemeId.Trim();
        if (lastBroadcastUiTheme != null &&
            string.Equals(lastBroadcastUiTheme, id, StringComparison.Ordinal))
            return;
        lastBroadcastUiTheme = id;
        try
        {
            var payload = new WsMessage { Type = "ui_theme_changed", UiThemeId = id };
            var json = JsonSerializer.Serialize(payload, JsonCtx.Default.WsMessage);
            _ = BroadcastUiThemeToAllSessionsAsync(sessionMgr, themeBroadcastLogger, json);
        }
        catch (Exception ex)
        {
            themeBroadcastLogger.LogWarning(ex, "Failed to serialize ui_theme_changed broadcast");
        }
    };
}

app.Logger.LogInformation("Office Copilot Server starting on {Urls}", app.Urls);
app.Run();

}
catch (Exception ex)
{
    Log.Fatal(ex, "Application terminated unexpectedly");
}
finally
{
    Log.CloseAndFlush();
}

static async Task HandleSession(
    WebSocket ws, string sessionId, SessionManager sessions,
    ChatService chatService, RpcManager rpcManager, HitlManager hitlManager, UserOptionsManager userOptionsManager, StreamCancelService streamCancelService, AttachmentCacheService attachmentCache, Microsoft.Extensions.Logging.ILogger logger)
{
    var buffer = new byte[4096];

    while (ws.State == WebSocketState.Open)
    {
        WebSocketReceiveResult result;
        using var ms = new MemoryStream();
        do
        {
            result = await ws.ReceiveAsync(buffer, CancellationToken.None);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                await ws.CloseAsync(
                    WebSocketCloseStatus.NormalClosure, "Bye", CancellationToken.None);
                return;
            }
            ms.Write(buffer, 0, result.Count);
        } while (!result.EndOfMessage);

        var raw = Encoding.UTF8.GetString(ms.ToArray());
        var msgType = GetMessageType(raw);
        logger.LogInformation("[{SessionId}] WS Recv type={Type} len={Len}", sessionId, msgType, raw.Length);

        WsMessage? incoming;
        try
        {
            incoming = JsonSerializer.Deserialize<WsMessage>(raw);
        }
        catch
        {
            incoming = new WsMessage { Type = "text", Content = raw };
        }

        if (incoming is null)
        {
            await SendJson(ws, new WsMessage { Type = "error", Content = "Empty message." });
            continue;
        }

        // Only require Content for message types that use it for chat (allow empty content when attachments present)
        var needsContent = incoming.Type is not ("ping" or "rpc_response" or "confirm_response" or "ask_options_response" or "get_debug_history" or "stop" or "set_context");
        var hasAttachments = incoming.Attachments is { Count: > 0 };
        if (needsContent && string.IsNullOrWhiteSpace(incoming.Content) && !hasAttachments)
        {
            await SendJson(ws, new WsMessage { Type = "error", Content = "Empty message." });
            continue;
        }

        switch (incoming.Type)
        {
            case "set_context":
                if (!string.IsNullOrEmpty(incoming.PageTitle))
                {
                    sessions.SetDisplayName(sessionId, incoming.PageTitle.Trim().Length > 200 ? incoming.PageTitle.Trim()[..200] : incoming.PageTitle.Trim());
                    logger.LogDebug("[{SessionId}] set_context displayName={DisplayName}", sessionId, incoming.PageTitle.Trim().Length > 50 ? incoming.PageTitle.Trim()[..50] + "…" : incoming.PageTitle.Trim());
                }
                break;
            case "ping":
                await SendJson(ws, new WsMessage { Type = "pong", Content = "pong" });
                break;
            case "rpc_response":
                if (incoming.Id != null)
                {
                    logger.LogDebug("[{SessionId}] RPC response id={ReqId} hasError={HasError}", sessionId, incoming.Id, incoming.Error != null);
                    rpcManager.HandleResponse(incoming.Id, incoming.Result, incoming.Error);
                }
                break;
            case "confirm_response":
                if (incoming.Id != null)
                {
                    var addToAllowList = incoming.AddToAllowList ?? false;
                    logger.LogDebug("[{SessionId}] HITL confirm_response id={ReqId} allowed={Allowed} addToAllowList={Add}", sessionId, incoming.Id, incoming.Allowed, addToAllowList);
                    hitlManager.HandleResponse(incoming.Id, incoming.Allowed ?? false, addToAllowList);
                }
                break;
            case "ask_options_response":
                if (incoming.Id != null)
                {
                    userOptionsManager.HandleResponse(incoming.Id, incoming.Selections);
                    logger.LogDebug("[{SessionId}] ask_options_response id={ReqId} stepCount={StepCount}",
                        sessionId, incoming.Id, incoming.Selections?.Count ?? 0);
                }
                break;
            case "get_debug_history":
                var history = chatService.GetSessionHistory(sessionId);
                var historyStr = string.Join("\n\n", history.Select(m => $"[{m.Role}]:\n{m.Content}"));
                await SendJson(ws, new WsMessage { Type = "debug_history", Content = historyStr });
                break;
            case "stop":
                streamCancelService.Cancel(sessionId);
                break;
            default:
                // 不 await，避免阻塞消息循环；否则工具发 rpc_request 后无法在同一连接上收到 rpc_response，导致超时
                _ = HandleChatStream(ws, sessionId, incoming, chatService, streamCancelService, attachmentCache, logger);
                break;
        }
    }
}

static async Task HandleChatStream(
    WebSocket ws, string sessionId, WsMessage incoming,
    ChatService chatService, StreamCancelService streamCancelService, AttachmentCacheService attachmentCache, Microsoft.Extensions.Logging.ILogger logger)
{
    var streamEndedByError = false;
    logger.LogDebug("[{SessionId}] Chat stream start, promptLen={Len}", sessionId, incoming.Content?.Length ?? 0);
    await SendJson(ws, new WsMessage { Type = "stream_start", SessionId = sessionId });

    var ct = streamCancelService.CreateForSession(sessionId);
    SessionContext.SetSessionId(sessionId);
    logger.LogDebug("[{SessionId}] SessionContext.SetSessionId({Sid})", sessionId, sessionId);

    // 将附件存为引用，只把 attachment:guid 写入对话，避免 base64 占满上下文
    List<string>? attachmentRefs = null;
    if (incoming.Attachments is { Count: > 0 })
    {
        attachmentRefs = new List<string>();
        foreach (var att in incoming.Attachments)
        {
            if (string.IsNullOrWhiteSpace(att.Data)) continue;
            try
            {
                var refId = attachmentCache.StoreFromBase64(att.Data, att.MimeType);
                attachmentRefs.Add(refId);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "[{SessionId}] Failed to store attachment, skipping", sessionId);
            }
        }
    }

    try
    {
        string prompt = incoming.Content ?? "";
        var reasoningParser = new ReasoningTagStreamParser();

        await foreach (var item in chatService.StreamChatAsync(sessionId, prompt, attachmentRefs?.Count > 0 ? null : incoming.Attachments, incoming.KnowledgeBaseId, incoming.Mode, incoming.PlanId, incoming.PlanCurrentStepIndex, attachmentRefs, ct))
        {
            if (item.IsWarning)
            {
                await SendJson(ws, new WsMessage { Type = "stream_warning", Content = item.Content });
                continue;
            }

            foreach (var part in reasoningParser.Append(item.Content))
            {
                if (string.IsNullOrEmpty(part.Text)) continue;
                var type = part.IsReasoning ? "reasoning_chunk" : "stream_chunk";
                await SendJson(ws, new WsMessage { Type = type, Content = part.Text });
            }
        }

        foreach (var part in reasoningParser.Flush())
        {
            if (string.IsNullOrEmpty(part.Text)) continue;
            var type = part.IsReasoning ? "reasoning_chunk" : "stream_chunk";
            await SendJson(ws, new WsMessage { Type = type, Content = part.Text });
        }
    }
    catch (OperationCanceledException)
    {
        logger.LogInformation("[{SessionId}] Chat stream stopped by user", sessionId);
    }
    catch (Exception ex)
    {
        var historyCount = 0;
        try { historyCount = chatService.GetSessionHistory(sessionId)?.Count ?? 0; } catch { /* ignore */ }
        logger.LogError(ex, "[{SessionId}] Chat stream error (history messages: {HistoryCount})", sessionId, historyCount);
        logger.LogError("[AI-RESPONSE-ERROR] 对方返回/异常全文 FullDetail={Detail}", ex.ToString());
        var friendlyMessage = ErrorMessageHelper.GetFriendlyMessage(ex);
        if (ws.State != WebSocketState.Open)
            logger.LogWarning("[{SessionId}] Skip sending error: WebSocket not open", sessionId);
        else
        {
            await SendJson(ws, new WsMessage
            {
                Type = "error",
                Content = friendlyMessage
            });
            logger.LogInformation("[{SessionId}] Chat stream error sent to client", sessionId);
        }
        streamEndedByError = true;
    }
    finally
    {
        streamCancelService.Remove(sessionId);
        SessionContext.SetSessionId(null);
        logger.LogDebug("[{SessionId}] SessionContext cleared", sessionId);
    }

    logger.LogInformation("[{SessionId}] Chat stream end {Suffix}", sessionId, streamEndedByError ? "(after error)" : "(completed)");
    if (ws.State != WebSocketState.Open)
        logger.LogWarning("[{SessionId}] Skip sending stream_end: WebSocket not open", sessionId);
    else
        await SendJson(ws, new WsMessage { Type = "stream_end" });
}

static string GetMessageType(string raw)
{
    try
    {
        using var doc = JsonDocument.Parse(raw);
        return doc.RootElement.TryGetProperty("type", out var t) ? t.GetString() ?? "?" : "?";
    }
    catch { return "?"; }
}

static async Task SendJson(WebSocket ws, WsMessage msg)
{
    if (ws.State != WebSocketState.Open) return;
    var json = JsonSerializer.Serialize(msg, JsonCtx.Default.WsMessage);
    var bytes = Encoding.UTF8.GetBytes(json);
    await ws.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None);
}

static async Task BroadcastUiThemeToAllSessionsAsync(SessionManager sessions, Microsoft.Extensions.Logging.ILogger logger, string json)
{
    try
    {
        await sessions.BroadcastToAllOpenAsync(json).ConfigureAwait(false);
    }
    catch (Exception ex)
    {
        logger.LogWarning(ex, "Broadcast ui_theme_changed failed");
    }
}

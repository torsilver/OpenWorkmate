using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using LLama.Native;
using OfficeCopilot.Server;
using OfficeCopilot.Server.Services;
using OfficeCopilot.Server.Services.Memory;
using OfficeCopilot.Server.Mcp;
using Serilog;

// 优先尝试 CUDA；若未装 CUDA 运行时（仅游戏驱动时 cuda12 的 DLL 会加载失败），则回退为默认加载，嵌入模型用 CPU
try
{
    if (OperatingSystem.IsWindows())
    {
        var cuda12Dir = Path.Combine(AppContext.BaseDirectory, "runtimes", "win-x64", "native", "cuda12");
        var cuda12Llama = Path.Combine(cuda12Dir, "llama.dll");
        if (File.Exists(cuda12Llama))
        {
            // 将 cuda12 目录加入 PATH，便于加载 cudart 等依赖（需已安装 CUDA Toolkit 12）
            var path = Environment.GetEnvironmentVariable("PATH") ?? "";
            if (!path.StartsWith(cuda12Dir, StringComparison.OrdinalIgnoreCase))
                Environment.SetEnvironmentVariable("PATH", cuda12Dir + Path.PathSeparator + path, EnvironmentVariableTarget.Process);
            NativeLibraryConfig.All.WithCuda(true);
        }
        else
        {
            NativeLibraryConfig.All.WithCuda(true);
        }
    }
    else
    {
        NativeLibraryConfig.All.WithCuda(true);
    }
}
catch { /* 已加载或配置失败时忽略 */ }

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Debug()
    .WriteTo.Console()
    .WriteTo.File("logs/office-copilot-.txt", rollingInterval: RollingInterval.Day)
    .CreateLogger();

try
{
    var builder = WebApplication.CreateBuilder(args);
    builder.Host.UseSerilog();

    builder.Services.AddSingleton<ConfigService>();
    builder.Services.AddSingleton<SkillService>();
    builder.Services.AddSingleton<ClawhubScriptRunner>();
    builder.Services.AddSingleton<McpClientManager>();
    builder.Services.AddSingleton<IKernelAccessor, KernelAccessor>();
    builder.Services.AddSingleton<EmbeddingProvider>();
    builder.Services.AddSingleton<IEmbeddingProvider>(sp => sp.GetRequiredService<EmbeddingProvider>());
    builder.Services.AddSingleton<IVectorStore>(sp =>
    {
        var config = sp.GetRequiredService<ConfigService>().Current;
        var t = (config.RagStorageType ?? "").Trim();
        var path = (config.RagStoragePath ?? "").Trim();
        if (string.Equals(t, "Sqlite", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(path))
        {
            path = Environment.ExpandEnvironmentVariables(path);
            if (!Path.IsPathRooted(path))
                path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OfficeCopilot", path);
            return new SqliteVectorStore("Data Source=" + path);
        }
        return new InMemoryVectorStore();
    });
    builder.Services.AddSingleton<IMemoryStoreService, MemoryStoreService>();
    builder.Services.AddSingleton<IEmbeddedToolSelectionModel, EmbeddedToolSelectionModel>();
    builder.Services.AddSingleton<IToolSelector, ToolSelectionService>();
    builder.Services.AddSingleton<SessionManager>();
    builder.Services.AddSingleton<RpcManager>();
    builder.Services.AddSingleton<HitlManager>();
    builder.Services.AddSingleton<ScreenshotCacheService>();
    builder.Services.AddSingleton<StreamCancelService>();
    builder.Services.AddSingleton<ChatService>();
    
    builder.Services.AddCors();

    var app = builder.Build();

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

    using var ws = await context.WebSockets.AcceptWebSocketAsync();
    app.Logger.LogInformation("Session {SessionId} connected", sessionId);

    sessions.Add(sessionId, ws);
    try
    {
        var rpcManager = app.Services.GetRequiredService<RpcManager>();
        var hitlManager = app.Services.GetRequiredService<HitlManager>();
        var streamCancelService = app.Services.GetRequiredService<StreamCancelService>();
        await HandleSession(ws, sessionId, sessions, chatService, rpcManager, hitlManager, streamCancelService, app.Logger);
    }
    finally
    {
        sessions.Remove(sessionId);
        app.Logger.LogInformation("Session {SessionId} disconnected", sessionId);
    }
});

app.MapGet("/health", () => Results.Ok(new { status = "running", time = DateTime.Now }));

app.Logger.LogInformation("WebSocket path={Path}, AuthRequired={Auth}, DevTokenAccepted={Dev}, AllowedOriginsCount={Count}",
    wsPath, !string.IsNullOrEmpty(authToken), isDev, allowedOrigins.Length);

app.MapGet("/api/config", (ConfigService config) => Results.Json(config.Current, JsonCtx.Default.AppConfig));
app.MapGet("/api/config/embedded-models", () =>
{
    var modelsDir = Path.Combine(AppContext.BaseDirectory, "Models");
    var list = new List<object>();
    if (Directory.Exists(modelsDir))
    {
        foreach (var path in Directory.EnumerateFiles(modelsDir, "*.gguf", SearchOption.TopDirectoryOnly))
        {
            var fullPath = Path.GetFullPath(path);
            var fileName = Path.GetFileName(path);
            list.Add(new { fileName, path = fullPath });
        }
    }
    return Results.Json(new { models = list });
});
app.MapPost("/api/config", async (HttpContext ctx, ConfigService config) =>
{
    var newConfig = await JsonSerializer.DeserializeAsync<AppConfig>(ctx.Request.Body, JsonCtx.Default.AppConfig);
    if (newConfig != null)
    {
        config.SaveConfig(newConfig);
        return Results.Ok();
    }
    return Results.BadRequest();
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
        return Results.Json(new { ok = false, message = "请求失败: " + (int)response.StatusCode + " " + response.ReasonPhrase + (string.IsNullOrEmpty(err) ? "" : " — " + err) }, statusCode: 200);
    }
    catch (TaskCanceledException)
    {
        return Results.Ok(new { ok = false, message = "连接超时，请检查接口地址或网络。" });
    }
    catch (Exception ex)
    {
        logger.LogWarning(ex, "Test AI exception");
        return Results.Ok(new { ok = false, message = "连接失败: " + ex.Message });
    }
});

app.MapGet("/api/tools/builtin", () =>
{
    var builtIn = new List<BuiltInPluginInfo>
    {
        new() { Id = "Browser", Name = "Browser", Description = "网页高亮、截图、运行页面脚本、整页截图等" },
        new() { Id = "File", Name = "File", Description = "保存截图到下载文件夹" },
        new() { Id = "CLI", Name = "CLI", Description = "执行白名单内系统命令" },
        new() { Id = "Excel", Name = "Excel", Description = "读写 Excel 文档" },
        new() { Id = "Word", Name = "Word", Description = "读写 Word 文档" },
        new() { Id = "Tavily", Name = "Tavily", Description = "Tavily 网页搜索与 URL 正文提取（需配置 TAVILY_API_KEY）" },
        new() { Id = "ClawhubSkill", Name = "ClawhubSkill", Description = "运行 Clawhub 可执行技能中的 node 脚本（无原生适配器时使用）" },
        new() { Id = "Memory", Name = "Memory", Description = "长期记忆：保存与检索用户偏好、关键事实（需配置 Embedding 模型）" }
    };
    return Results.Json(builtIn, JsonCtx.Default.ListBuiltInPluginInfo);
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
    return Results.BadRequest();
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

app.MapGet("/api/memory", async (string? sessionId, int skip, int take, IMemoryStoreService memory) =>
{
    if (!memory.IsAvailable)
        return Results.Json(new { ok = false, message = "未配置 Embedding 模型。" }, statusCode: 400);
    var list = await memory.ListAsync(sessionId, Math.Max(0, skip), Math.Clamp(take, 1, 100)).ConfigureAwait(false);
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
    var id = await memory.SaveAsync(null, body.Text.Trim(), body.SessionId, metadata).ConfigureAwait(false);
    return Results.Json(new { ok = true, id });
});

app.MapGet("/api/memory/{id}", async (string id, IMemoryStoreService memory) =>
{
    var item = await memory.GetAsync(id).ConfigureAwait(false);
    if (item == null) return Results.NotFound();
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
    var metadata = string.IsNullOrWhiteSpace(body.Tags) ? null : new Dictionary<string, string> { ["tags"] = body.Tags };
    await memory.SaveAsync(id, body.Text.Trim(), sessionId, metadata).ConfigureAwait(false);
    return Results.Ok(new { ok = true });
});

app.MapDelete("/api/memory/{id}", async (string id, IMemoryStoreService memory) =>
{
    var deleted = await memory.DeleteAsync(id).ConfigureAwait(false);
    if (!deleted) return Results.NotFound();
    return Results.Ok(new { ok = true });
});

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
    ChatService chatService, RpcManager rpcManager, HitlManager hitlManager, StreamCancelService streamCancelService, Microsoft.Extensions.Logging.ILogger logger)
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
        var preview = raw.Length > 200 ? raw[..200] + "..." : raw;
        logger.LogInformation("[{SessionId}] WS Recv type={Type} len={Len} preview={Preview}", sessionId, msgType, raw.Length, preview);

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
        var needsContent = incoming.Type is not ("ping" or "mode_change" or "rpc_response" or "confirm_response" or "get_debug_history" or "stop");
        var hasAttachments = incoming.Attachments is { Count: > 0 };
        if (needsContent && string.IsNullOrWhiteSpace(incoming.Content) && !hasAttachments)
        {
            await SendJson(ws, new WsMessage { Type = "error", Content = "Empty message." });
            continue;
        }

        switch (incoming.Type)
        {
            case "ping":
                await SendJson(ws, new WsMessage { Type = "pong", Content = "pong" });
                break;
            case "mode_change":
                logger.LogInformation("[{SessionId}] Mode changed to: {Mode}", sessionId, incoming.Content);
                chatService.SetSessionMode(sessionId, incoming.Content);
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
                    logger.LogDebug("[{SessionId}] HITL confirm_response id={ReqId} allowed={Allowed}", sessionId, incoming.Id, incoming.Allowed);
                    hitlManager.HandleResponse(incoming.Id, incoming.Allowed ?? false);
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
                _ = HandleChatStream(ws, sessionId, incoming, chatService, streamCancelService, logger);
                break;
        }
    }
}

static async Task HandleChatStream(
    WebSocket ws, string sessionId, WsMessage incoming,
    ChatService chatService, StreamCancelService streamCancelService, Microsoft.Extensions.Logging.ILogger logger)
{
    var streamEndedByError = false;
    logger.LogInformation("[{SessionId}] Chat stream start, promptLen={Len}", sessionId, incoming.Content?.Length ?? 0);
    await SendJson(ws, new WsMessage { Type = "stream_start", SessionId = sessionId });

    var ct = streamCancelService.CreateForSession(sessionId);
    SessionContext.SetSessionId(sessionId);
    logger.LogDebug("[{SessionId}] SessionContext.SetSessionId({Sid})", sessionId, sessionId);
    try
    {
        string prompt = incoming.Content ?? "";
        if (incoming.Type == "text_with_context" && incoming.Context != null)
        {
            prompt = $"[当前网页上下文]\n标题: {incoming.Context.Title}\nURL: {incoming.Context.Url}\n内容:\n{incoming.Context.Content}\n\n[用户输入]\n{incoming.Content}";
        }

        await foreach (var chunk in chatService.StreamChatAsync(sessionId, prompt, incoming.Attachments, incoming.KnowledgeBaseId, ct))
        {
            await SendJson(ws, new WsMessage { Type = "stream_chunk", Content = chunk });
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

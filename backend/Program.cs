using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using OfficeCopilot.Server;
using OfficeCopilot.Server.Services;
using OfficeCopilot.Server.Mcp;
using Serilog;

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
    builder.Services.AddSingleton<McpClientManager>();
builder.Services.AddSingleton<SessionManager>();
builder.Services.AddSingleton<RpcManager>();
builder.Services.AddSingleton<HitlManager>();
builder.Services.AddSingleton<ScreenshotCacheService>();
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
    if (!string.IsNullOrEmpty(authToken) && token != authToken)
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
        await HandleSession(ws, sessionId, sessions, chatService, rpcManager, hitlManager, app.Logger);
    }
    finally
    {
        sessions.Remove(sessionId);
        app.Logger.LogInformation("Session {SessionId} disconnected", sessionId);
    }
});

app.MapGet("/health", () => Results.Ok(new { status = "running", time = DateTime.Now }));

app.MapGet("/api/config", (ConfigService config) => Results.Json(config.Current, JsonCtx.Default.AppConfig));
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

app.MapGet("/api/tools/builtin", () =>
{
    var builtIn = new List<BuiltInPluginInfo>
    {
        new() { Id = "Browser", Name = "Browser", Description = "网页高亮、截图、运行页面脚本、整页截图等" },
        new() { Id = "File", Name = "File", Description = "保存截图到下载文件夹" },
        new() { Id = "CLI", Name = "CLI", Description = "执行白名单内系统命令" },
        new() { Id = "Excel", Name = "Excel", Description = "读写 Excel 文档" },
        new() { Id = "Word", Name = "Word", Description = "读写 Word 文档" }
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
    ChatService chatService, RpcManager rpcManager, HitlManager hitlManager, Microsoft.Extensions.Logging.ILogger logger)
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

        // Only require Content for message types that use it for chat
        var needsContent = incoming.Type is not ("ping" or "mode_change" or "rpc_response" or "confirm_response" or "get_debug_history");
        if (needsContent && string.IsNullOrWhiteSpace(incoming.Content))
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
            default:
                // 不 await，避免阻塞消息循环；否则工具发 rpc_request 后无法在同一连接上收到 rpc_response，导致超时
                _ = HandleChatStream(ws, sessionId, incoming, chatService, logger);
                break;
        }
    }
}

static async Task HandleChatStream(
    WebSocket ws, string sessionId, WsMessage incoming,
    ChatService chatService, Microsoft.Extensions.Logging.ILogger logger)
{
    logger.LogInformation("[{SessionId}] Chat stream start, promptLen={Len}", sessionId, incoming.Content?.Length ?? 0);
    await SendJson(ws, new WsMessage { Type = "stream_start", SessionId = sessionId });

    SessionContext.SetSessionId(sessionId);
    logger.LogDebug("[{SessionId}] SessionContext.SetSessionId({Sid})", sessionId, sessionId);
    try
    {
        string prompt = incoming.Content;
        if (incoming.Type == "text_with_context" && incoming.Context != null)
        {
            prompt = $"[当前网页上下文]\n标题: {incoming.Context.Title}\nURL: {incoming.Context.Url}\n内容:\n{incoming.Context.Content}\n\n[用户输入]\n{incoming.Content}";
        }

        await foreach (var chunk in chatService.StreamChatAsync(sessionId, prompt))
        {
            await SendJson(ws, new WsMessage { Type = "stream_chunk", Content = chunk });
        }
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "[{SessionId}] Chat stream error", sessionId);
        var friendlyMessage = ErrorMessageHelper.GetFriendlyMessage(ex);
        await SendJson(ws, new WsMessage
        {
            Type = "error",
            Content = friendlyMessage
        });
    }
    finally
    {
        SessionContext.SetSessionId(null);
        logger.LogDebug("[{SessionId}] SessionContext cleared", sessionId);
    }

    logger.LogInformation("[{SessionId}] Chat stream end", sessionId);
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

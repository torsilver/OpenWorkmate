using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using OfficeCopilot.Server;

namespace OfficeCopilot.Server.Security;

/// <summary>
/// 保护 <c>/api/*</c>：有效密钥为 user-config <c>webSocketAuthToken</c> 优先，否则 appsettings <c>WebSocket:AuthToken</c>。
/// 未配置有效密钥时仅允许本机回环（与 TestServer 中 RemoteIpAddress 为 null 时放行一致）。
/// </summary>
public sealed class LocalApiAuthMiddleware
{
    private const string HeaderName = "X-OfficeCopilot-Token";
    private readonly RequestDelegate _next;
    private readonly ConfigService _configService;
    private readonly bool _isDevelopment;
    private readonly JsonSerializerOptions _jsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public LocalApiAuthMiddleware(RequestDelegate next, ConfigService configService, IWebHostEnvironment environment)
    {
        _next = next;
        _configService = configService;
        _isDevelopment = environment.IsDevelopment();
    }

    public async Task InvokeAsync(HttpContext context)
    {
        if (!context.Request.Path.StartsWithSegments("/api"))
        {
            await _next(context);
            return;
        }

        // 本机 loopback 仅返回访问密钥，供 WPS/Office/扩展首次自动同步（不暴露其它配置）
        if (IsBootstrapLocalServiceAuthPath(context.Request.Path)
            && HttpMethods.IsGet(context.Request.Method)
            && DebugLogHelper.IsDebugLogLoopback(context))
        {
            await _next(context);
            return;
        }

        // 本机 loopback：调试统计与调试日志网页（logs.html）调用的 API 不便带头；Map 内仍有 IsDebugLogLoopback 校验
        if (IsDebugLoopbackDiagnosticsPath(context.Request.Path) && DebugLogHelper.IsDebugLogLoopback(context))
        {
            await _next(context);
            return;
        }

        var authToken = _configService.GetEffectiveWebSocketAuthToken();
        if (string.IsNullOrEmpty(authToken))
        {
            if (!IsLocalOrTestConnection(context))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                context.Response.ContentType = "application/json; charset=utf-8";
                await context.Response.WriteAsync(JsonSerializer.Serialize(new
                {
                    ok = false,
                    message = "服务端未配置 WebSocket:AuthToken 时，HTTP API 仅允许本机（loopback）访问。请在 appsettings.json 中设置强随机 WebSocket:AuthToken，并在扩展选项页填写相同密钥。"
                }, _jsonOptions));
                return;
            }
        }
        else
        {
            if (!TryGetBearerToken(context.Request, out var presented) ||
                !string.Equals(presented, authToken, StringComparison.Ordinal))
            {
                if (_isDevelopment && string.Equals(presented, ProgramAuthConstants.DevelopmentWsToken, StringComparison.Ordinal))
                {
                    await _next(context);
                    return;
                }

                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                context.Response.ContentType = "application/json; charset=utf-8";
                await context.Response.WriteAsync(JsonSerializer.Serialize(new
                {
                    ok = false,
                    message = "未授权：请在请求头携带 X-OfficeCopilot-Token 或 Authorization: Bearer，值须与本机配置中的访问密钥一致（扩展选项页或 user-config 的 webSocketAuthToken / appsettings 的 WebSocket:AuthToken）。"
                }, _jsonOptions));
                return;
            }
        }

        await _next(context);
    }

    private static bool IsLocalOrTestConnection(HttpContext context)
    {
        var ip = context.Connection.RemoteIpAddress;
        if (ip == null) return true;
        return IPAddress.IsLoopback(ip);
    }

    private static bool TryGetBearerToken(HttpRequest request, out string token)
    {
        token = "";
        if (request.Headers.TryGetValue(HeaderName, out var hv))
        {
            var v = hv.ToString().Trim();
            if (!string.IsNullOrEmpty(v))
            {
                token = v;
                return true;
            }
        }

        var auth = request.Headers.Authorization.ToString();
        if (!string.IsNullOrEmpty(auth) && auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            token = auth["Bearer ".Length..].Trim();
            return !string.IsNullOrEmpty(token);
        }

        return false;
    }

    private static bool IsDebugLoopbackDiagnosticsPath(PathString path)
    {
        var p = path.Value ?? "";
        if (p.StartsWith("/api/debug/agent-stats", StringComparison.OrdinalIgnoreCase))
            return true;
        if (string.Equals(p, "/api/debug/log-files", StringComparison.OrdinalIgnoreCase))
            return true;
        if (string.Equals(p, "/api/debug/log-tail", StringComparison.OrdinalIgnoreCase))
            return true;
        return false;
    }

    private static bool IsBootstrapLocalServiceAuthPath(PathString path)
    {
        var p = path.Value ?? "";
        return string.Equals(p, "/api/bootstrap/local-service-auth", StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>与 Program.cs 中开发用 WebSocket token 常量保持一致（供中间件引用）。</summary>
public static class ProgramAuthConstants
{
    public const string DevelopmentWsToken = "office-copilot-dev-token";
}

using System.Net;
using System.Net.Mime;
using System.Text.Json;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Options;
using Serilog;
using Taskly.AI.Gateway;
using Taskly.AI.Gateway.Endpoints;
using Taskly.AI.Gateway.Models;
using Taskly.AI.Gateway.Services;
using Taskly.AI.Gateway.Storage;

var builder = WebApplication.CreateBuilder(args);

builder.Host.UseSerilog((ctx, _, lc) =>
{
    lc.ReadFrom.Configuration(ctx.Configuration)
        .Enrich.FromLogContext();
});

builder.Services.Configure<AiGatewayOptions>(builder.Configuration.GetSection(AiGatewayOptions.SectionName));
builder.Services.AddSingleton<TelemetryPolicyResolver>();
builder.Services.AddSingleton<UserPolicyStore>();
builder.Services.AddSingleton<TelemetryAggregatedPolicyBuilder>();
builder.Services.AddSingleton<SessionsIndex>();
builder.Services.AddSingleton<BlobStore>();
builder.Services.AddSingleton<SessionJsonlWriter>();
builder.Services.AddHttpClient("llm-upstream", c => c.Timeout = TimeSpan.FromMinutes(10));
builder.Services.AddOpenApi();
builder.Services.AddAuthorization();
builder.Services.AddCors(o => o.AddDefaultPolicy(p =>
    p.AllowAnyHeader().AllowAnyMethod().SetIsOriginAllowed(_ => true)));

var app = builder.Build();

try
{
    var dataRootOpt = app.Services.GetRequiredService<IOptions<AiGatewayOptions>>();
    var root = Path.GetFullPath(dataRootOpt.Value.DataRoot);
    Directory.CreateDirectory(root);
    Directory.CreateDirectory(Path.Combine(root, "sessions"));
    Directory.CreateDirectory(Path.Combine(root, "blobs"));
    Directory.CreateDirectory(Path.Combine(root, "index"));
    app.Services.GetRequiredService<SessionsIndex>().LoadOrRebuild();
}
catch (Exception ex)
{
    Log.Warning(ex, "AI Gateway: could not initialize data directories");
}

app.UseSerilogRequestLogging();
app.UseForwardedHeaders(new ForwardedHeadersOptions
{
    ForwardedHeaders = ForwardedHeaders.None
});
app.UseCors();
app.UseDefaultFiles();
app.UseStaticFiles();

if (app.Environment.IsDevelopment())
    app.MapOpenApi();

var jsonOpts = new JsonSerializerOptions
{
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true,
    ReadCommentHandling = JsonCommentHandling.Skip,
    AllowTrailingCommas = true
};

var policy = app.MapGroup("/api/policy");
policy.MapGet("/transmission", (HttpContext http, TelemetryPolicyResolver policies) =>
    {
        if (!TelemetryAuth.ValidatePolicyApiKey(http, app.Configuration))
            return Results.Json(new { ok = false, message = "Unauthorized." }, statusCode: StatusCodes.Status401Unauthorized);
        var p = policies.GetTransmissionPolicy();
        return Results.Json(p, jsonOpts);
    })
    .WithName("TransmissionPolicy")
    .WithTags("policy");

policy.MapGet("/aggregated", (HttpContext http, TelemetryAggregatedPolicyBuilder agg) =>
    {
        if (!TelemetryAuth.ValidatePolicyApiKey(http, app.Configuration))
            return Results.Json(new { ok = false, message = "Unauthorized." }, statusCode: StatusCodes.Status401Unauthorized);

        var profileId = http.Request.Query["profileId"].ToString().Trim();
        if (string.IsNullOrEmpty(profileId))
            profileId = null;
        var (envelope, etagHeader) = agg.BuildEnvelope(profileId);
        foreach (var raw in http.Request.Headers.IfNoneMatch)
        {
            var t = (raw ?? "").Trim().Trim('"');
            if (string.Equals(t, envelope.ETag, StringComparison.OrdinalIgnoreCase))
                return Results.StatusCode(StatusCodes.Status304NotModified);
        }
        http.Response.Headers.ETag = etagHeader;
        http.Response.Headers.CacheControl = "private, max-age=0, must-revalidate";
        return Results.Json(envelope, jsonOpts);
    })
    .WithName("AggregatedPolicy")
    .WithTags("policy");

var userPolicy = policy.MapGroup("/user");
userPolicy.AddEndpointFilter(async (invocationContext, next) =>
{
    var ip = invocationContext.HttpContext.Connection.RemoteIpAddress;
    if (ip is null || !IPAddress.IsLoopback(ip))
        return Results.Json(new { ok = false, message = "Forbidden (loopback only)." }, statusCode: 403);
    return await next(invocationContext);
});
userPolicy.MapGet("/", (UserPolicyStore store) => Results.Json(store.ReadOrDefault(), jsonOpts));
userPolicy.MapPut("/", async (HttpContext http, UserPolicyStore store, CancellationToken ct) =>
{
    UserPolicyFile? body;
    try
    {
        body = await JsonSerializer.DeserializeAsync<UserPolicyFile>(http.Request.Body, jsonOpts, ct).ConfigureAwait(false);
    }
    catch (JsonException ex)
    {
        return Results.Json(new { ok = false, message = "Invalid JSON: " + ex.Message }, statusCode: 400);
    }
    if (body is null)
        return Results.Json(new { ok = false, message = "Empty body." }, statusCode: 400);
    store.Write(body);
    return Results.Json(new { ok = true });
});

var admin = app.MapGroup("/api/admin");
admin.AddEndpointFilter(async (invocationContext, next) =>
{
    var http = invocationContext.HttpContext;
    var cfg = http.RequestServices.GetRequiredService<IConfiguration>();
    var adminKey = (cfg.GetSection(AiGatewayOptions.SectionName)["AdminApiKey"] ?? "").Trim();
    var provided = (http.Request.Headers["X-Admin-Key"].FirstOrDefault() ?? "").Trim();
    var ip = http.Connection.RemoteIpAddress;
    var loopback = ip != null && IPAddress.IsLoopback(ip);
    if (!string.IsNullOrEmpty(adminKey) && provided == adminKey)
        return await next(invocationContext);
    if (string.IsNullOrEmpty(adminKey) && loopback)
        return await next(invocationContext);
    return Results.Json(new { ok = false, message = "Forbidden." }, statusCode: StatusCodes.Status403Forbidden);
});

admin.MapGet("/policy", (TelemetryPolicyResolver policies) =>
{
    var bundle = policies.GetBundleForDisplay();
    return Results.Json(bundle, jsonOpts);
});

admin.MapPut("/policy", async Task<IResult> (HttpContext http, TelemetryPolicyResolver policies, CancellationToken ct) =>
{
    OpsPolicyBundle? body;
    try
    {
        body = await JsonSerializer.DeserializeAsync<OpsPolicyBundle>(http.Request.Body, jsonOpts, ct)
            .ConfigureAwait(false);
    }
    catch (JsonException ex)
    {
        return Results.Json(new { ok = false, message = "请求体不是合法 JSON：" + ex.Message }, statusCode: 400);
    }

    if (body is null)
        return Results.Json(new { ok = false, message = "请求体解析失败，请确认 JSON 格式与字段类型。" }, statusCode: 400);

    try
    {
        policies.WriteFullBundle(body);
    }
    catch (Exception ex)
    {
        return Results.Json(new { ok = false, message = "写入策略文件失败：" + ex.Message }, statusCode: 500);
    }

    return Results.Json(new { ok = true });
});

admin.MapGet("/devices", (IOptionsSnapshot<AiGatewayOptions> opt) =>
{
    var root = Path.GetFullPath(opt.Value.DataRoot);
    var devices = Path.Combine(root, "devices");
    if (!Directory.Exists(devices))
        return Results.Json(Array.Empty<string>());
    var ids = Directory.GetDirectories(devices)
        .Select(Path.GetFileName)
        .Where(s => !string.IsNullOrEmpty(s))
        .ToArray();
    return Results.Json(ids);
});

admin.MapGet("/devices/{deviceId}/override", (string deviceId, TelemetryPolicyResolver policies) =>
{
    if (!TelemetryPathValidator.IsValidDeviceId(deviceId))
        return Results.Json(new { ok = false, message = "Invalid deviceId." }, statusCode: 400);
    var o = policies.ReadOverride(deviceId);
    return Results.Json(o ?? new TelemetryOverrideFile());
});

admin.MapPut("/devices/{deviceId}/override", async Task<IResult> (string deviceId, HttpContext http, TelemetryPolicyResolver policies, CancellationToken ct) =>
{
    if (!TelemetryPathValidator.IsValidDeviceId(deviceId))
        return Results.Json(new { ok = false, message = "Invalid deviceId." }, statusCode: 400);
    var body = await JsonSerializer.DeserializeAsync<TelemetryOverrideFile>(http.Request.Body, jsonOpts, ct)
        .ConfigureAwait(false);
    if (body is null)
        return Results.Json(new { ok = false, message = "Invalid body." }, statusCode: 400);
    policies.WriteOverride(deviceId, body);
    return Results.Json(new { ok = true });
});

admin.MapDelete("/devices/{deviceId}/override", (string deviceId, TelemetryPolicyResolver policies) =>
{
    if (!TelemetryPathValidator.IsValidDeviceId(deviceId))
        return Results.Json(new { ok = false, message = "Invalid deviceId." }, statusCode: 400);
    policies.DeleteOverride(deviceId);
    return Results.Json(new { ok = true });
});

admin.MapPost("/telemetry/generate-api-key", async Task<IResult> (
    IWebHostEnvironment env,
    IConfiguration configuration,
    CancellationToken ct) =>
{
    var path = Path.Combine(env.ContentRootPath, "appsettings.json");
    if (!File.Exists(path))
        return Results.Json(new { ok = false, message = "未找到 appsettings.json，无法写入 AiGateway:ApiKey。" }, statusCode: 500);

    string newKey;
    try
    {
        newKey = TelemetryAppsettingsApiKeyWriter.GenerateApiKey();
        await TelemetryAppsettingsApiKeyWriter.WriteTelemetryApiKeyAsync(path, newKey, ct).ConfigureAwait(false);
    }
    catch (Exception ex)
    {
        return Results.Json(new { ok = false, message = "写入配置失败：" + ex.Message }, statusCode: 500);
    }

    if (configuration is IConfigurationRoot root)
        root.Reload();

    return Results.Json(new { ok = true, apiKey = newKey });
});

admin.MapGet("/health", () => Results.Text("ok", MediaTypeNames.Text.Plain));

app.MapLlmChatCompletions();
app.MapIngest(jsonOpts);
app.MapMy(jsonOpts);

app.MapGet("/", () => Results.Redirect("/admin.html", permanent: false));

string MaskKey(string? s)
{
    if (string.IsNullOrWhiteSpace(s)) return "(empty)";
    var t = s.Trim();
    if (t.Length <= 8) return "***";
    return t[..4] + "…" + t[^4..];
}

try
{
    var opt = app.Services.GetRequiredService<IOptions<AiGatewayOptions>>().Value;
    var fullData = Path.GetFullPath(opt.DataRoot);
    var urls = app.Urls.Count > 0 ? string.Join(", ", app.Urls) : "(not bound yet)";
    Log.Information(
        "AI Gateway startup: urls={Urls}, dataRoot={DataRoot}, resolved={Resolved}, policyApiKeyConfigured={PolicyKeyOk}, policyApiKeyMask={PolicyKeyMask}, adminApiKeyConfigured={AdminKeyOk}, retentionDays={Days}, maxEventPayloadChars={Max}",
        urls,
        opt.DataRoot,
        fullData,
        !string.IsNullOrWhiteSpace(opt.ApiKey),
        MaskKey(opt.ApiKey),
        !string.IsNullOrWhiteSpace(opt.AdminApiKey),
        opt.RetentionDays,
        opt.MaxEventPayloadChars);
}
catch (Exception ex)
{
    Log.Warning(ex, "AI Gateway: failed to log startup configuration snapshot");
}

app.Run();

using System.Net;
using System.Net.Mime;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Serilog;
using Taskly.Telemetry.Relay;
using Taskly.Telemetry.Relay.Models;
using Taskly.Telemetry.Relay.Services;

var builder = WebApplication.CreateBuilder(args);

// 勿再 WriteTo.Console：appsettings.json 的 Serilog:WriteTo 已含 Console，重复注册会导致每条日志在控制台出现两遍。
builder.Host.UseSerilog((ctx, services, lc) => lc
    .ReadFrom.Configuration(ctx.Configuration)
    .Enrich.FromLogContext());

builder.Services.Configure<TelemetryOptions>(builder.Configuration.GetSection(TelemetryOptions.SectionName));
builder.Services.AddSingleton<TelemetryPolicyResolver>();
builder.Services.AddSingleton<TelemetrySessionWriter>();
builder.Services.AddSingleton<TelemetryIngestService>();
builder.Services.AddHostedService<TelemetryRetentionBackgroundService>();
builder.Services.AddOpenApi();
builder.Services.AddAuthorization();
builder.Services.AddCors(o => o.AddDefaultPolicy(p =>
    p.AllowAnyHeader().AllowAnyMethod().SetIsOriginAllowed(_ => true)));

var app = builder.Build();

try
{
    var dataRootOpt = app.Services.GetRequiredService<IOptions<TelemetryOptions>>();
    var root = Path.GetFullPath(dataRootOpt.Value.DataRoot);
    Directory.CreateDirectory(root);
}
catch (Exception ex)
{
    // 启动失败时仍由后续写入路径报错；此处仅尽量保证 data 根目录存在
    Log.Warning(ex, "Telemetry relay: could not create DataRoot directory");
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

app.MapPost("/ingest/batch", async (HttpContext http, TelemetryIngestService ingest, CancellationToken ct) =>
    {
        if (!TelemetryAuth.ValidateIngestApiKey(http, app.Configuration))
            return Results.Json(new { ok = false, message = "Unauthorized." }, statusCode: StatusCodes.Status401Unauthorized);

        IngestBatchRequest? body;
        try
        {
            body = await JsonSerializer.DeserializeAsync<IngestBatchRequest>(http.Request.Body, jsonOpts, ct)
                .ConfigureAwait(false);
        }
        catch
        {
            return Results.Json(new { ok = false, message = "Invalid JSON body." }, statusCode: StatusCodes.Status400BadRequest);
        }

        if (body?.Events is not { Count: > 0 })
            return Results.Json(new { ok = false, message = "events required." }, statusCode: StatusCodes.Status400BadRequest);

        try
        {
            var (accepted, skipped) = await ingest.IngestAsync(body, ct).ConfigureAwait(false);
            return Results.Json(new { ok = true, accepted, skipped });
        }
        catch (ArgumentException ex)
        {
            return Results.Json(new { ok = false, message = ex.Message }, statusCode: StatusCodes.Status400BadRequest);
        }
    })
    .WithName("IngestBatch")
    .WithTags("telemetry");

app.MapGet("/policy/transmission", (HttpContext http, TelemetryPolicyResolver policies) =>
    {
        if (!TelemetryAuth.ValidateIngestApiKey(http, app.Configuration))
            return Results.Json(new { ok = false, message = "Unauthorized." }, statusCode: StatusCodes.Status401Unauthorized);
        var p = policies.GetTransmissionPolicy();
        return Results.Json(p, jsonOpts);
    })
    .WithName("TransmissionPolicy")
    .WithTags("telemetry");

var admin = app.MapGroup("/admin");
admin.AddEndpointFilter(async (invocationContext, next) =>
{
    var http = invocationContext.HttpContext;
    var cfg = http.RequestServices.GetRequiredService<IConfiguration>();
    var adminKey = (cfg.GetSection(TelemetryOptions.SectionName)["AdminApiKey"] ?? "").Trim();
    var provided = (http.Request.Headers["X-Admin-Key"].FirstOrDefault() ?? "").Trim();
    var ip = http.Connection.RemoteIpAddress;
    var loopback = ip != null && IPAddress.IsLoopback(ip);
    if (!string.IsNullOrEmpty(adminKey) && provided == adminKey)
        return await next(invocationContext);
    if (string.IsNullOrEmpty(adminKey) && loopback)
        return await next(invocationContext);
    return Results.Json(new { ok = false, message = "Forbidden." }, statusCode: StatusCodes.Status403Forbidden);
});

admin.MapGet("/defaults", (TelemetryPolicyResolver policies) =>
{
    var d = policies.ReadDefaultsOnly() ?? new TelemetryDefaultsFile();
    return Results.Json(d);
});

admin.MapPut("/defaults", async Task<IResult> (HttpContext http, TelemetryPolicyResolver policies, CancellationToken ct) =>
{
    var body = await JsonSerializer.DeserializeAsync<TelemetryDefaultsFile>(http.Request.Body, jsonOpts, ct)
        .ConfigureAwait(false);
    if (body is null)
        return Results.Json(new { ok = false, message = "Invalid body." }, statusCode: 400);
    policies.WriteDefaults(body);
    return Results.Json(new { ok = true });
});

admin.MapGet("/devices", (IOptionsSnapshot<TelemetryOptions> opt) =>
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
        return Results.Json(new { ok = false, message = "未找到 appsettings.json，无法写入 Telemetry:ApiKey。" }, statusCode: 500);

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

app.MapGet("/", () => Results.Redirect("/admin.html", permanent: false));

string MaskTelemetryKey(string? s)
{
    if (string.IsNullOrWhiteSpace(s)) return "(empty)";
    var t = s.Trim();
    if (t.Length <= 8) return "***";
    return t[..4] + "…" + t[^4..];
}

try
{
    var opt = app.Services.GetRequiredService<IOptions<TelemetryOptions>>().Value;
    var fullData = Path.GetFullPath(opt.DataRoot);
    var urls = app.Urls.Count > 0 ? string.Join(", ", app.Urls) : "(not bound yet)";
    Log.Information(
        "Telemetry relay startup: urls={Urls}, dataRoot={DataRoot}, dataRootResolved={Resolved}, ingestApiKeyConfigured={IngestKeyOk}, ingestApiKeyMask={IngestKeyMask}, adminApiKeyConfigured={AdminKeyOk}, retentionDays={Days}, maxEventPayloadChars={Max}",
        urls,
        opt.DataRoot,
        fullData,
        !string.IsNullOrWhiteSpace(opt.ApiKey),
        MaskTelemetryKey(opt.ApiKey),
        !string.IsNullOrWhiteSpace(opt.AdminApiKey),
        opt.RetentionDays,
        opt.MaxEventPayloadChars);
}
catch (Exception ex)
{
    Log.Warning(ex, "Telemetry relay: failed to log startup configuration snapshot");
}

app.Run();

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using OpenWorkmate.Server;

namespace OpenWorkmate.Server.Services.Telemetry;

/// <summary>与 <c>start-ai-and-gateway.cmd</c>、选项页占位符一致的本机开发默认。</summary>
public static class TelemetryRelayDefaults
{
    public const string LocalDevBaseUrl = "http://127.0.0.1:8777";

    /// <summary>与 <see cref="TelemetryTransmissionPolicyBackgroundService"/> 拉取策略使用的根 URL 一致（含 Key 非空时的本机默认）。</summary>
    public static string GetEffectiveRelayBaseUrl(AppConfig cfg)
    {
        var apiKey = (cfg.AiGatewayApiKey ?? "").Trim();
        var baseUrl = (cfg.AiGatewayBaseUrl ?? "").Trim().TrimEnd('/');
        if (string.IsNullOrEmpty(baseUrl) && cfg.TelemetryEnabled && !string.IsNullOrEmpty(apiKey))
            baseUrl = LocalDevBaseUrl.TrimEnd('/');
        return baseUrl;
    }

    /// <summary>日志脱敏：仅示意前后若干字符。</summary>
    public static string MaskSecret(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "(empty)";
        var t = s.Trim();
        if (t.Length <= 8) return "***";
        return t[..4] + "…" + t[^4..];
    }

    /// <summary>AI 后台启动时打印遥测相关配置：Gateway URL + span ingest。</summary>
    public static void LogOutboundRelayConfig(ILogger logger, AppConfig cfg, IConfiguration hostConfiguration)
    {
        _ = hostConfiguration;
        var raw = (cfg.AiGatewayBaseUrl ?? "").Trim();
        var eff = GetEffectiveRelayBaseUrl(cfg);
        var policyOk = cfg.TelemetryEnabled && !string.IsNullOrEmpty(eff) && !string.IsNullOrEmpty((cfg.AiGatewayApiKey ?? "").Trim());
        var willIngestSpans = cfg.TelemetryEnabled && policyOk;
        logger.LogInformation(
            "AI Gateway client: enabled={Enabled}, aiGatewayBaseUrlEffective={EffUrl}, gatewayApiKey={KeyMask}, policyFetchOk={PolicyOk}, willPostSpansToGateway={WillIngest}",
            cfg.TelemetryEnabled,
            string.IsNullOrEmpty(eff) ? "(none)" : eff,
            MaskSecret(cfg.AiGatewayApiKey),
            policyOk,
            willIngestSpans);
        if (!string.IsNullOrEmpty(raw))
            logger.LogDebug("AI Gateway baseUrlRaw={RawUrl}", raw);
    }
}

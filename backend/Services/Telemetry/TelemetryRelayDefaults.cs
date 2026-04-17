using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using OfficeCopilot.Server;

namespace OfficeCopilot.Server.Services.Telemetry;

/// <summary>与 <c>start-ai-and-telemetry.cmd</c>、选项页占位符一致的本机开发默认。</summary>
public static class TelemetryRelayDefaults
{
    public const string LocalDevBaseUrl = "http://127.0.0.1:8777";

    /// <summary>与 <see cref="TelemetryTransmissionPolicyBackgroundService"/> 拉取策略使用的根 URL 一致（含 Key 非空时的本机默认）。</summary>
    public static string GetEffectiveRelayBaseUrl(AppConfig cfg)
    {
        var apiKey = (cfg.TelemetryRelayApiKey ?? "").Trim();
        var baseUrl = (cfg.TelemetryRelayBaseUrl ?? "").Trim().TrimEnd('/');
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

    /// <summary>AI 后台启动时打印遥测相关配置：策略拉取（中继）与观测落库（Seq）。</summary>
    public static void LogOutboundRelayConfig(ILogger logger, AppConfig cfg, IConfiguration hostConfiguration)
    {
        var raw = (cfg.TelemetryRelayBaseUrl ?? "").Trim();
        var eff = GetEffectiveRelayBaseUrl(cfg);
        var seqUrl = (hostConfiguration["Telemetry:SeqServerUrl"] ?? "").Trim();
        var seqConfigured = !string.IsNullOrEmpty(seqUrl);
        var policyOk = cfg.TelemetryEnabled && !string.IsNullOrEmpty(eff) && !string.IsNullOrEmpty((cfg.TelemetryRelayApiKey ?? "").Trim());
        var willEmitToSeq = cfg.TelemetryEnabled && seqConfigured;
        logger.LogInformation(
            "Telemetry: enabled={Enabled}, relayPolicyUrlEffective={EffUrl}, relayApiKey={KeyMask}, policyFetchOk={PolicyOk}, seqServerConfigured={SeqConfigured}, willEmitTelemetryToSeq={WillEmit}",
            cfg.TelemetryEnabled,
            string.IsNullOrEmpty(eff) ? "(none)" : eff,
            MaskSecret(cfg.TelemetryRelayApiKey),
            policyOk,
            seqConfigured,
            willEmitToSeq);
        if (!string.IsNullOrEmpty(raw))
            logger.LogDebug("Telemetry relayBaseUrlRaw={RawUrl}", raw);
    }
}

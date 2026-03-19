using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OfficeCopilot.Server.Services;

/// <summary>
/// 语音转文字（STT）上游适配器：
/// - Whisper 兼容接口：/audio/transcriptions 或 /v1/audio/transcriptions（multipart/form-data，返回 { "text": "..." }）
/// - 阿里百炼 Qwen-ASR OpenAI 兼容：compatible-mode/v1/chat/completions（JSON，messages + input_audio，返回 choices[0].message.content）
/// </summary>
public static class SttUpstreamAdapter
{
    public enum UpstreamKind
    {
        WhisperCompatible,
        DashScopeQwenOpenAICompatible
    }

    public static string NormalizeEndpoint(string endpoint)
    {
        return (endpoint ?? string.Empty).Trim().TrimEnd('/');
    }

    public static UpstreamKind ResolveMode(string endpoint)
    {
        var ep = NormalizeEndpoint(endpoint);
        if (string.IsNullOrWhiteSpace(ep))
            throw new InvalidOperationException("语音转写接口地址无效。");

        // 常见拼写错误：compatible-model/v1（少了一个 e）
        if (ep.Contains("compatible-model/v1", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("接口地址路径疑似写错：你填的是 compatible-model/v1，但阿里百炼为 compatible-mode/v1。");
        }

        if (ep.Contains("compatible-mode/v1", StringComparison.OrdinalIgnoreCase))
            return UpstreamKind.DashScopeQwenOpenAICompatible;

        return UpstreamKind.WhisperCompatible;
    }

    public static string BuildWhisperTranscriptionsUrl(string endpoint)
    {
        // endpoint 若本身包含 /v1，则直接拼 /audio/transcriptions；
        // 否则补上 /v1/audio/transcriptions
        var ep = NormalizeEndpoint(endpoint);
        return ep.Contains("/v1", StringComparison.OrdinalIgnoreCase)
            ? ep + "/audio/transcriptions"
            : ep + "/v1/audio/transcriptions";
    }

    public static string BuildDashScopeChatCompletionsUrl(string endpoint)
    {
        var ep = NormalizeEndpoint(endpoint);
        return ep + "/chat/completions";
    }

    public static string BuildAudioDataUrl(byte[] audioBytes, string contentType)
    {
        var ct = string.IsNullOrWhiteSpace(contentType) ? "audio/mpeg" : contentType.Trim();
        var base64 = Convert.ToBase64String(audioBytes);
        return "data:" + ct + ";base64," + base64;
    }

    public static void ValidateDashScopeModelId(string modelId)
    {
        var mid = (modelId ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(mid))
            throw new InvalidOperationException("DashScope 兼容模式需要填写模型 ID（如 qwen3-asr-flash）。");

        // 目前仅做这一条最小闭环，避免用户随便填其它模型导致继续 404。
        if (!string.Equals(mid, "qwen3-asr-flash", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("DashScope 兼容模式目前仅支持模型 qwen3-asr-flash（你填的是 " + mid + "）。");
    }

    public static string BuildDashScopeOpenAICompatibleRequestJson(string modelId, string dataUrl, string? language)
    {
        ValidateDashScopeModelId(modelId);
        if (string.IsNullOrWhiteSpace(dataUrl))
            throw new InvalidOperationException("语音数据无效。");

        // 注意：asr_options 在文档中是 OpenAI 兼容请求体的非标准字段，
        // 用于透传给语音识别引擎（通过 extra_body 的语义）。
        var payload = new Dictionary<string, object?>
        {
            ["model"] = modelId,
            ["messages"] = new object[]
            {
                new
                {
                    role = "user",
                    content = new object[]
                    {
                        new
                        {
                            type = "input_audio",
                            input_audio = new { data = dataUrl }
                        }
                    }
                }
            }
        };

        if (!string.IsNullOrWhiteSpace(language))
        {
            // enable_itn 默认 false；我们不强制开关，仅保证 language 提示生效
            payload["asr_options"] = new { language = language.Trim() };
        }

        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };
        return JsonSerializer.Serialize(payload, options);
    }

    public static string ExtractTranscriptFromDashScopeResponse(string responseText)
    {
        if (string.IsNullOrWhiteSpace(responseText))
            return string.Empty;

        using var doc = JsonDocument.Parse(responseText);
        var root = doc.RootElement;

        // 非流式：choices[0].message.content
        if (root.TryGetProperty("choices", out var choices) && choices.ValueKind == JsonValueKind.Array && choices.GetArrayLength() > 0)
        {
            var first = choices[0];
            if (first.TryGetProperty("message", out var message) &&
                message.ValueKind == JsonValueKind.Object &&
                message.TryGetProperty("content", out var content) &&
                content.ValueKind == JsonValueKind.String)
            {
                return content.GetString() ?? string.Empty;
            }
        }

        // 兜底：有的实现会直接返回 text（不是 OpenAI 兼容标准，但留个兜底）
        if (root.TryGetProperty("text", out var textEl) && textEl.ValueKind == JsonValueKind.String)
            return textEl.GetString() ?? string.Empty;

        return string.Empty;
    }

    /// <summary>
    /// 生成一个标准 PCM WAV（16kHz/16bit/单声道），用于“测试连接”场景的最小音频。
    /// 直接写死的极简 WAV header 在部分转录服务会被判定为非法。
    /// </summary>
    public static byte[] BuildMinimalWavPcm16kMono(int durationMs = 100)
    {
        if (durationMs <= 0) durationMs = 100;
        const int sampleRate = 16000;
        const short bitsPerSample = 16;
        const short channels = 1;
        const short audioFormat = 1; // PCM

        var numSamples = (int)Math.Round(sampleRate * (durationMs / 1000.0));
        if (numSamples < 1) numSamples = 1;

        var blockAlign = (short)(channels * (bitsPerSample / 8));
        var byteRate = sampleRate * blockAlign;
        var dataSize = numSamples * blockAlign;

        // RIFF chunk size = 4 + (8 + fmtChunkSize) + (8 + dataSize) = 36 + dataSize
        var riffChunkSize = 36 + dataSize;

        using var ms = new MemoryStream(44 + dataSize);
        using var bw = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true);

        // RIFF header
        bw.Write(Encoding.ASCII.GetBytes("RIFF"));
        bw.Write(riffChunkSize);
        bw.Write(Encoding.ASCII.GetBytes("WAVE"));

        // fmt chunk
        bw.Write(Encoding.ASCII.GetBytes("fmt "));
        bw.Write(16); // PCM fmt chunk size
        bw.Write(audioFormat);
        bw.Write(channels);
        bw.Write(sampleRate);
        bw.Write(byteRate);
        bw.Write(blockAlign);
        bw.Write(bitsPerSample);

        // data chunk
        bw.Write(Encoding.ASCII.GetBytes("data"));
        bw.Write(dataSize);

        // 生成静音 PCM：全 0
        for (var i = 0; i < numSamples; i++)
            bw.Write((short)0);

        bw.Flush();
        return ms.ToArray();
    }
}


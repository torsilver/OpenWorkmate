using System;
using System.Text.Json;
using OfficeCopilot.Server.Services;
using Xunit;

namespace backend.Tests.Unit;

public class SttUpstreamAdapterTests
{
    [Fact]
    public void ResolveMode_CompatibleMode_ReturnsDashScope()
    {
        var endpoint = "https://dashscope.aliyuncs.com/compatible-mode/v1";
        var kind = SttUpstreamAdapter.ResolveMode(endpoint);
        Assert.Equal(SttUpstreamAdapter.UpstreamKind.DashScopeQwenOpenAICompatible, kind);
    }

    [Fact]
    public void ResolveMode_CompatibleModelTypo_ThrowsHelpfulMessage()
    {
        var endpoint = "https://dashscope.aliyuncs.com/compatible-model/v1";
        var ex = Assert.Throws<InvalidOperationException>(() => SttUpstreamAdapter.ResolveMode(endpoint));
        Assert.Contains("compatible-model", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("compatible-mode", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BuildDashScopeChatCompletionsUrl_NormalizesSlash()
    {
        var endpoint = "https://dashscope.aliyuncs.com/compatible-mode/v1/";
        var url = SttUpstreamAdapter.BuildDashScopeChatCompletionsUrl(endpoint);
        Assert.Equal("https://dashscope.aliyuncs.com/compatible-mode/v1/chat/completions", url);
    }

    [Fact]
    public void BuildDashScopeOpenAICompatibleRequestJson_ContainsInputAudioAndModel()
    {
        var modelId = "qwen3-asr-flash";
        var audioBytes = new byte[] { 0x01, 0x02 };
        var dataUrl = SttUpstreamAdapter.BuildAudioDataUrl(audioBytes, "audio/wav");
        Assert.StartsWith("data:audio/wav;base64,", dataUrl, StringComparison.OrdinalIgnoreCase);

        var json = SttUpstreamAdapter.BuildDashScopeOpenAICompatibleRequestJson(modelId, dataUrl, language: null);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.Equal(modelId, root.GetProperty("model").GetString());

        var messages = root.GetProperty("messages");
        Assert.Equal(JsonValueKind.Array, messages.ValueKind);
        Assert.Equal(1, messages.GetArrayLength());
        var content0 = messages[0].GetProperty("content")[0];
        Assert.Equal("input_audio", content0.GetProperty("type").GetString());
        Assert.Equal(dataUrl, content0.GetProperty("input_audio").GetProperty("data").GetString());

        Assert.False(root.TryGetProperty("asr_options", out _));
    }

    [Fact]
    public void BuildDashScopeOpenAICompatibleRequestJson_IncludesAsrOptionsWhenLanguageProvided()
    {
        var modelId = "qwen3-asr-flash";
        var dataUrl = "data:audio/wav;base64,AAAA";
        var json = SttUpstreamAdapter.BuildDashScopeOpenAICompatibleRequestJson(modelId, dataUrl, language: "zh");
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        Assert.True(root.TryGetProperty("asr_options", out var asr));
        Assert.Equal("zh", asr.GetProperty("language").GetString());
    }

    [Fact]
    public void ExtractTranscriptFromDashScopeResponse_ReturnsChoicesMessageContent()
    {
        var responseText = """
        {
          "choices": [
            { "message": { "content": "hello world" } }
          ]
        }
        """;

        var text = SttUpstreamAdapter.ExtractTranscriptFromDashScopeResponse(responseText);
        Assert.Equal("hello world", text);
    }
}


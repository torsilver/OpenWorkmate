using System;
using System.Text.Json;
using OpenWorkmate.Server.Services;
using Xunit;

namespace backend.Tests.Unit;

public class OcrUpstreamAdapterTests
{
    [Fact]
    public void BuildDashScopeChatCompletionsUrl_AppendsChatCompletions()
    {
        var endpoint = "https://dashscope.aliyuncs.com/compatible-mode/v1";
        var url = OcrUpstreamAdapter.BuildDashScopeChatCompletionsUrl(endpoint);
        Assert.Equal("https://dashscope.aliyuncs.com/compatible-mode/v1/chat/completions", url);
    }

    [Fact]
    public void BuildDashScopeChatCompletionsUrl_DoesNotDuplicate()
    {
        var endpoint = "https://dashscope.aliyuncs.com/compatible-mode/v1/chat/completions";
        var url = OcrUpstreamAdapter.BuildDashScopeChatCompletionsUrl(endpoint);
        Assert.Equal(endpoint, url);
    }

    [Fact]
    public void BuildDashScopeOpenAICompatibleOcrRequestJson_IncludesImageUrlAndPrompt()
    {
        var modelId = "qwen-vl-ocr-latest";
        var dataUrl = "data:image/png;base64,AAAA";
        var prompt = "請只输出图片中的识别文字。";
        var json = OcrUpstreamAdapter.BuildDashScopeOpenAICompatibleOcrRequestJson(modelId, dataUrl, prompt, "zh");

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        Assert.Equal(modelId, root.GetProperty("model").GetString());

        var messages = root.GetProperty("messages");
        Assert.Equal(1, messages.GetArrayLength());
        var content = messages[0].GetProperty("content");
        Assert.Equal(2, content.GetArrayLength());

        var imageEl = content[0];
        Assert.Equal("image_url", imageEl.GetProperty("type").GetString());
        Assert.Equal(dataUrl, imageEl.GetProperty("image_url").GetProperty("url").GetString());

        var textEl = content[1];
        Assert.Equal("text", textEl.GetProperty("type").GetString());
        var text = textEl.GetProperty("text").GetString();
        Assert.NotNull(text);
        Assert.Contains(prompt, text!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("识别语言提示：zh", text!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BuildDashScopeOpenAICompatibleOcrRequestJson_LockedModel_IgnoresQwenLanguageOverride()
    {
        var modelId = "qwen-vl-ocr-latest";
        var dataUrl = "data:image/png;base64,AAAA";
        var prompt = "hello";
        var json = OcrUpstreamAdapter.BuildDashScopeOpenAICompatibleOcrRequestJson(
            modelId, dataUrl, prompt, "qwen-vl-from-language", configuredModelId: "user-locked-model");

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        Assert.Equal("user-locked-model", root.GetProperty("model").GetString());
    }

    [Fact]
    public void BuildDashScopeOpenAICompatibleOcrRequestJson_UsesModelOverride_WhenLanguageHintIsQwen()
    {
        var modelId = "qwen-vl-ocr-latest";
        var dataUrl = "data:image/png;base64,AAAA";
        var prompt = "hello";
        var json = OcrUpstreamAdapter.BuildDashScopeOpenAICompatibleOcrRequestJson(modelId, dataUrl, prompt, "qwen-vl-ocr-special");

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        Assert.Equal("qwen-vl-ocr-special", root.GetProperty("model").GetString());
    }

    [Fact]
    public void ExtractOcrTextFromChatCompletionsResponse_ReturnsMessageContent()
    {
        var responseText = """
        {
          "choices": [
            {
              "message": {
                "content": "hello world"
              }
            }
          ]
        }
        """;
        var text = OcrUpstreamAdapter.ExtractOcrTextFromChatCompletionsResponse(responseText);
        Assert.Equal("hello world", text);
    }
}


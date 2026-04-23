using System.Text;
using System.Text.Json;
using OfficeCopilot.Server.Services.ModelProfiles;
using Xunit;

namespace backend.Tests.Unit;

public sealed class ModelProfileRegistryTests
{
    [Fact]
    public void Reload_MergesVendorAndOverlay_FromTempDirectory()
    {
        var dir = Path.Combine(Path.GetTempPath(), "taskly-model-profiles-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(dir, "ModelProfiles", "vendor"));
        File.WriteAllText(
            Path.Combine(dir, "ModelProfiles", "vendor", "model_prices_excerpt.json"),
            $$"""
            {
              "moonshot/kimi-k2.6": {
                "max_input_tokens": 262144,
                "supports_vision": true,
                "supports_function_calling": true,
                "supports_reasoning": true
              }
            }
            """);

        File.WriteAllText(
            Path.Combine(dir, "ModelProfiles", "taskly-overlay.json"),
            """
            {
              "moonshot/kimi-k2.6": {
                "requiresReasoningEchoWithTools": true,
                "suppressUpstreamThinkingWithTools": true,
                "notes": "test"
              }
            }
            """);

        var reg = new ModelProfileRegistry();
        reg.Reload(dir);

        Assert.True(reg.TryGetMerged("moonshot/kimi-k2.6", out var p));
        Assert.NotNull(p);
        Assert.Equal(262144, p!.MaxInputTokens);
        Assert.True(p.SupportsVision);
        Assert.True(p.RequiresReasoningEchoWithTools);
        Assert.True(p.SuppressUpstreamThinkingWithTools);
        Assert.Equal("test", p.Notes);
    }

    [Fact]
    public void TryGetMerged_UnknownKey_ReturnsFalse()
    {
        var reg = new ModelProfileRegistry();
        reg.Reload(Path.Combine(Path.GetTempPath(), "nonexistent-" + Guid.NewGuid().ToString("N")));
        Assert.False(reg.TryGetMerged("moonshot/kimi-k2.6", out _));
    }

    [Fact]
    public void TryMergeThinkingSuppress_WhenToolCallsWithoutReasoning_AddsThinkingFalse()
    {
        var json = """
            {
              "model": "kimi-k2-6",
              "messages": [
                {"role":"user","content":"hi"},
                {"role":"assistant","content":"","tool_calls":[{"id":"1","type":"function","function":{"name":"f","arguments":"{}"}}]},
                {"role":"tool","tool_call_id":"1","content":"{}"}
              ],
              "stream": true
            }
            """;
        var utf8 = Encoding.UTF8.GetBytes(json);
        var merged = ModelProfileChatRequestMergeHandler.TryMergeThinkingSuppress(utf8);
        Assert.NotNull(merged);
        using var doc = JsonDocument.Parse(merged);
        Assert.True(doc.RootElement.TryGetProperty("thinking", out var t));
        Assert.Equal(JsonValueKind.False, t.ValueKind);
    }

    [Fact]
    public void TryMergeThinkingSuppress_WhenReasoningPresent_ReturnsNull()
    {
        var json = """
            {
              "messages": [
                {"role":"assistant","content":"","reasoning_content":"x","tool_calls":[{"id":"1","type":"function","function":{"name":"f","arguments":"{}"}}]}
              ]
            }
            """;
        var merged = ModelProfileChatRequestMergeHandler.TryMergeThinkingSuppress(Encoding.UTF8.GetBytes(json));
        Assert.Null(merged);
    }
}

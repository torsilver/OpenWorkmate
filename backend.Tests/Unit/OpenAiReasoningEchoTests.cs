using System.Text;
using System.Text.Json;
using OpenWorkmate.Server.Services.ModelProfiles;
using OpenWorkmate.Server.Services.OpenAiCompat;
using Xunit;

namespace backend.Tests.Unit;

public sealed class OpenAiReasoningEchoTests
{
    [Fact]
    public void TryPatchRequestUtf8_InjectsReasoningAndThinkingKeep()
    {
        var sid = "session-patch-1";
        OpenAiReasoningEchoStore.AppendAfterToolAssistantRound(sid, "alpha-reasoning");
        var body = """
                   {"model":"kimi-k2.6","messages":[{"role":"assistant","content":"","tool_calls":[{"id":"c1","type":"function","function":{"name":"x","arguments":"{}"}}]},{"role":"tool","tool_call_id":"c1","content":"{}"}],"stream":true}
                   """u8.ToArray();
        var profile = new MergedModelProfile
        {
            ProfileKey = "moonshot/kimi-k2.6",
            UseThinkingKeepAll = true
        };
        var patched = OpenAiReasoningEchoMessagePatch.TryPatchRequestUtf8(body, sid, profile, null);
        Assert.NotNull(patched);
        using var doc = JsonDocument.Parse(Encoding.UTF8.GetString(patched));
        var root = doc.RootElement;
        Assert.True(root.TryGetProperty("thinking", out var thinking));
        Assert.Equal("enabled", thinking.GetProperty("type").GetString());
        Assert.Equal("all", thinking.GetProperty("keep").GetString());
        var msgs = root.GetProperty("messages");
        var assistant = msgs[0];
        Assert.Equal("alpha-reasoning", assistant.GetProperty("reasoning_content").GetString());
    }

    [Fact]
    public void TryPatchRequestUtf8_WithoutUseThinkingKeepAll_DoesNotWriteThinkingObject()
    {
        var sid = "session-patch-2";
        OpenAiReasoningEchoStore.AppendAfterToolAssistantRound(sid, "r2");
        var body = """
                   {"model":"m","messages":[{"role":"assistant","content":"","tool_calls":[{"id":"c1","type":"function","function":{"name":"x","arguments":"{}"}}]}],"stream":true}
                   """u8.ToArray();
        var profile = new MergedModelProfile { ProfileKey = "other", UseThinkingKeepAll = false };
        var patched = OpenAiReasoningEchoMessagePatch.TryPatchRequestUtf8(body, sid, profile, null);
        Assert.NotNull(patched);
        using var doc = JsonDocument.Parse(Encoding.UTF8.GetString(patched));
        Assert.False(doc.RootElement.TryGetProperty("thinking", out _));
    }

    [Fact]
    public void TryPatchRequestUtf8_MergeThinkingKeepOnly_WhenNoMissingReasoning()
    {
        var sid = "session-patch-3";
        var body = """
                   {"model":"m","messages":[{"role":"user","content":"hi"}],"stream":true}
                   """u8.ToArray();
        var profile = new MergedModelProfile { ProfileKey = "moonshot/kimi-k2.6", UseThinkingKeepAll = true };
        var patched = OpenAiReasoningEchoMessagePatch.TryPatchRequestUtf8(body, sid, profile, null);
        Assert.NotNull(patched);
        using var doc = JsonDocument.Parse(Encoding.UTF8.GetString(patched));
        Assert.True(doc.RootElement.TryGetProperty("thinking", out var thinking));
        Assert.Equal("all", thinking.GetProperty("keep").GetString());
    }

    [Fact]
    public void JsonLineIndicatesToolCalls_TrueForDeltaToolCalls()
    {
        var line = """{"choices":[{"delta":{"tool_calls":[{"index":0,"id":"x"}]}}]}""";
        Assert.True(OpenAiReasoningEchoSseHelpers.JsonLineIndicatesToolCalls(line));
    }

    [Fact]
    public void JsonLineIndicatesToolCalls_TrueForFinishReason()
    {
        var line = """{"choices":[{"finish_reason":"tool_calls"}]}""";
        Assert.True(OpenAiReasoningEchoSseHelpers.JsonLineIndicatesToolCalls(line));
    }

    [Fact]
    public void JsonLineIndicatesToolCalls_FalseForContentOnly()
    {
        var line = """{"choices":[{"delta":{"content":"hello"}}]}""";
        Assert.False(OpenAiReasoningEchoSseHelpers.JsonLineIndicatesToolCalls(line));
    }

    [Fact]
    public void TryPatchRequestUtf8_TwoAssistantToolRounds_InjectedByOrdinal()
    {
        var sid = "session-two-assistants";
        OpenAiReasoningEchoStore.AppendAfterToolAssistantRound(sid, "reasoning-round-0");
        OpenAiReasoningEchoStore.AppendAfterToolAssistantRound(sid, "reasoning-round-1");
        var body = """
                   {"model":"kimi-k2.6","stream":true,"messages":[
                     {"role":"assistant","content":"","tool_calls":[{"id":"c0","type":"function","function":{"name":"a","arguments":"{}"}}]},
                     {"role":"tool","tool_call_id":"c0","content":"{}"},
                     {"role":"assistant","content":"","tool_calls":[{"id":"c1","type":"function","function":{"name":"b","arguments":"{}"}}]}
                   ]}
                   """u8.ToArray();
        var patched = OpenAiReasoningEchoMessagePatch.TryPatchRequestUtf8(body, sid, null, null);
        Assert.NotNull(patched);
        using var doc = JsonDocument.Parse(Encoding.UTF8.GetString(patched!));
        var msgs = doc.RootElement.GetProperty("messages");
        Assert.Equal("reasoning-round-0", msgs[0].GetProperty("reasoning_content").GetString());
        Assert.Equal("reasoning-round-1", msgs[2].GetProperty("reasoning_content").GetString());
    }

    [Fact]
    public void TryPatchRequestUtf8_FirstAssistantAlreadyHasReasoning_SecondUsesNextOrdinal()
    {
        var sid = "session-ordinal-skip";
        OpenAiReasoningEchoStore.AppendAfterToolAssistantRound(sid, "store-0");
        OpenAiReasoningEchoStore.AppendAfterToolAssistantRound(sid, "store-1");
        var body = """
                   {"model":"m","stream":true,"messages":[
                     {"role":"assistant","reasoning_content":"persisted","content":"","tool_calls":[{"id":"c0","type":"function","function":{"name":"a","arguments":"{}"}}]},
                     {"role":"tool","tool_call_id":"c0","content":"{}"},
                     {"role":"assistant","content":"","tool_calls":[{"id":"c1","type":"function","function":{"name":"b","arguments":"{}"}}]}
                   ]}
                   """u8.ToArray();
        var patched = OpenAiReasoningEchoMessagePatch.TryPatchRequestUtf8(body, sid, null, null);
        Assert.NotNull(patched);
        using var doc = JsonDocument.Parse(Encoding.UTF8.GetString(patched!));
        var msgs = doc.RootElement.GetProperty("messages");
        Assert.Equal("persisted", msgs[0].GetProperty("reasoning_content").GetString());
        Assert.Equal("store-1", msgs[2].GetProperty("reasoning_content").GetString());
    }

    [Fact]
    public void TryPatchRequestUtf8_StripsEmptyTextPartsWhenReasoningAlreadyPresent_NoInjectionNeeded()
    {
        var sid = "sanitize-array-only";
        var body = """
                   {"model":"kimi-k2.6","stream":true,"messages":[
                     {"role":"assistant","reasoning_content":"already","content":[{"type":"text","text":""},{"type":"text","text":"hi"}],"tool_calls":[{"id":"1","type":"function","function":{"name":"f","arguments":"{}"}}]}
                   ]}
                   """u8.ToArray();
        var patched = OpenAiReasoningEchoMessagePatch.TryPatchRequestUtf8(body, sid, null, null);
        Assert.NotNull(patched);
        using var doc = JsonDocument.Parse(Encoding.UTF8.GetString(patched!));
        var content = doc.RootElement.GetProperty("messages")[0].GetProperty("content");
        Assert.Equal(JsonValueKind.Array, content.ValueKind);
        Assert.Equal(1, content.GetArrayLength());
        Assert.Equal("hi", content[0].GetProperty("text").GetString());
    }

    [Fact]
    public void TryPatchRequestUtf8_StripsEmptyTextPartsAndInjectsReasoning()
    {
        var sid = "sanitize-plus-reasoning";
        OpenAiReasoningEchoStore.AppendAfterToolAssistantRound(sid, "echo-reasoning");
        var body = """
                   {"model":"m","stream":true,"messages":[
                     {"role":"assistant","content":[{"type":"text","text":""},{"type":"text","text":"visible"}],"tool_calls":[{"id":"c","type":"function","function":{"name":"x","arguments":"{}"}}]}
                   ]}
                   """u8.ToArray();
        var patched = OpenAiReasoningEchoMessagePatch.TryPatchRequestUtf8(body, sid, null, null);
        Assert.NotNull(patched);
        using var doc = JsonDocument.Parse(Encoding.UTF8.GetString(patched!));
        var a = doc.RootElement.GetProperty("messages")[0];
        Assert.Equal("echo-reasoning", a.GetProperty("reasoning_content").GetString());
        var content = a.GetProperty("content");
        Assert.Equal(JsonValueKind.Array, content.ValueKind);
        Assert.Single(content.EnumerateArray());
        Assert.Equal("visible", content[0].GetProperty("text").GetString());
    }

    [Fact]
    public void TryPatchRequestUtf8_AllTextPartsEmpty_ContentBecomesPlaceholderSpace()
    {
        var sid = "sanitize-all-empty";
        var body = """
                   {"model":"m","stream":true,"messages":[
                     {"role":"assistant","reasoning_content":"keep","content":[{"type":"text","text":""},{"type":"text","text":""}],"tool_calls":[{"id":"c","type":"function","function":{"name":"x","arguments":"{}"}}]}
                   ]}
                   """u8.ToArray();
        var patched = OpenAiReasoningEchoMessagePatch.TryPatchRequestUtf8(body, sid, null, null);
        Assert.NotNull(patched);
        using var doc = JsonDocument.Parse(Encoding.UTF8.GetString(patched!));
        var content = doc.RootElement.GetProperty("messages")[0].GetProperty("content");
        Assert.Equal(JsonValueKind.String, content.ValueKind);
        Assert.Equal(" ", content.GetString());
    }

    [Fact]
    public void BuildMessagesSummary_ContentArray_LogsPartTypesAndEmptyTextMarkers()
    {
        var json = """
                   {"messages":[{"role":"assistant","content":[{"type":"text","text":""},{"type":"text","text":"hi"}],"tool_calls":[{"id":"1","type":"function","function":{"name":"f","arguments":"{}"}}]}]}
                   """;
        using var doc = JsonDocument.Parse(json);
        var s = OpenAiReasoningEchoDiagnostics.BuildMessagesSummary(doc.RootElement.GetProperty("messages"));
        Assert.Contains("contentParts=2", s);
        Assert.Contains("parts=", s);
        Assert.Contains("EMPTY_TX", s);
        Assert.Contains("WARN:assistantToolsEmptyTextPart", s);
        Assert.Contains("[0:text", s);
        Assert.Contains("[1:text", s);
    }
}

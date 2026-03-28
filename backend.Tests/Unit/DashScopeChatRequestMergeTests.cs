using System.Text;
using System.Text.Json;
using OfficeCopilot.Server;
using OfficeCopilot.Server.Services.DashScope;
using Xunit;

namespace backend.Tests.Unit;

public sealed class DashScopeChatRequestMergeTests
{
    [Theory]
    [InlineData("https://dashscope.aliyuncs.com/compatible-mode/v1/chat/completions", true)]
    [InlineData("https://dashscope-intl.aliyuncs.com/compatible-mode/v1/chat/completions", true)]
    [InlineData("https://api.openai.com/v1/chat/completions", false)]
    [InlineData("https://example.com/v1/chat/completions", false)]
    public void IsDashScopeChatCompletions_host_and_path(string url, bool expected)
    {
        var u = new Uri(url);
        Assert.Equal(expected, DashScopeChatRequestMerge.IsDashScopeChatCompletions(u));
    }

    [Fact]
    public void Merge_no_entry_no_background_returns_null()
    {
        var body = """{"model":"qwen-plus","messages":[],"stream":true}"""u8.ToArray();
        var merged = DashScopeChatRequestMerge.MergeChatCompletionUtf8Body(body, null);
        Assert.Null(merged);
    }

    [Fact]
    public void Merge_enable_thinking_true_inserts_field()
    {
        var body = """{"model":"qwen-plus","messages":[],"stream":true}"""u8.ToArray();
        var entry = new AiModelEntry { Id = "x", EnableThinking = true };
        var merged = DashScopeChatRequestMerge.MergeChatCompletionUtf8Body(body, entry);
        Assert.NotNull(merged);
        using var doc = JsonDocument.Parse(merged);
        Assert.True(doc.RootElement.GetProperty("enable_thinking").GetBoolean());
        Assert.True(doc.RootElement.GetProperty("stream").GetBoolean());
    }

    [Fact]
    public void Merge_background_forces_enable_thinking_false()
    {
        var body = """{"model":"qwen-plus","messages":[],"stream":true,"enable_thinking":true}"""u8.ToArray();
        var entry = new AiModelEntry { Id = "x", DisableThinkingForBackgroundCalls = true };
        using (DashScopeCallKindContext.EnterBackground())
        {
            var merged = DashScopeChatRequestMerge.MergeChatCompletionUtf8Body(body, entry);
            Assert.NotNull(merged);
            using var doc = JsonDocument.Parse(merged);
            Assert.False(doc.RootElement.GetProperty("enable_thinking").GetBoolean());
        }
    }

    [Fact]
    public void RequestBodyIndicatesStream()
    {
        Assert.True(DashScopeChatRequestMerge.RequestBodyIndicatesStream("""{"stream":true}"""u8.ToArray()));
        Assert.False(DashScopeChatRequestMerge.RequestBodyIndicatesStream("""{"stream":false}"""u8.ToArray()));
    }

    [Fact]
    public void TryExtractReasoningContent_emits_delta()
    {
        var emitted = new List<string>();
        DashScopeSseReasoningTapStream.TryExtractReasoningContent(
            """{"choices":[{"delta":{"reasoning_content":"你好"}}]}""",
            s => emitted.Add(s));
        Assert.Single(emitted);
        Assert.Equal("你好", emitted[0]);
    }
}

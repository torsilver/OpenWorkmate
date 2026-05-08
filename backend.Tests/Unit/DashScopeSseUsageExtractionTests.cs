using OpenWorkmate.Server.Services.DashScope;
using Xunit;

namespace backend.Tests.Unit;

public sealed class DashScopeSseUsageExtractionTests
{
    [Fact]
    public void TryExtractTopLevelUsageJson_emits_usage_object_raw_text()
    {
        const string line =
            """{"id":"x","choices":[],"usage":{"prompt_tokens":1,"completion_tokens":2,"total_tokens":3}}""";
        string? got = null;
        DashScopeSseReasoningTapStream.TryExtractTopLevelUsageJson(line, s => got = s);
        Assert.Equal("""{"prompt_tokens":1,"completion_tokens":2,"total_tokens":3}""", got);
    }

    [Fact]
    public void TryExtractTopLevelUsageJson_without_usage_does_not_emit()
    {
        const string line = """{"choices":[{"delta":{"content":"hi"}}]}""";
        var called = false;
        DashScopeSseReasoningTapStream.TryExtractTopLevelUsageJson(line, _ => called = true);
        Assert.False(called);
    }

    [Fact]
    public void TryExtractTopLevelUsageJson_ignores_non_object_usage()
    {
        const string line = """{"usage":null,"choices":[]}""";
        var called = false;
        DashScopeSseReasoningTapStream.TryExtractTopLevelUsageJson(line, _ => called = true);
        Assert.False(called);
    }

    [Fact]
    public void TryExtractReasoningContent_emits_reasoning_content_string()
    {
        const string line =
            """{"choices":[{"delta":{"reasoning_content":"think"}}]}""";
        var got = "";
        DashScopeSseReasoningTapStream.TryExtractReasoningContent(line, s => got += s);
        Assert.Equal("think", got);
    }
}

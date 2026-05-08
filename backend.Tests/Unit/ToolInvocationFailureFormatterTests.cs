using OpenWorkmate.Server.Services.ToolInvocation;
using Xunit;

namespace backend.Tests.Unit;

public sealed class ToolInvocationFailureFormatterTests
{
    [Fact]
    public void FormatToolInvocationFailure_ArgumentException_includes_plugin_func_and_message()
    {
        var ex = new ArgumentException("The arguments dictionary is missing a value for the required parameter 'title'.", "arguments");
        var s = ToolInvocationFailureFormatter.FormatToolInvocationFailure("Word", "word_document_create", ex);
        Assert.Contains("Word.word_document_create", s);
        Assert.Contains("title", s);
        Assert.StartsWith("[工具调用失败]", s);
    }

    [Fact]
    public void FormatToolInvocationFailure_InvalidOperationException_preserves_detail()
    {
        var ex = new InvalidOperationException("bad state");
        var s = ToolInvocationFailureFormatter.FormatToolInvocationFailure("CLI", "run_command", ex);
        Assert.Contains("CLI.run_command", s);
        Assert.Contains("bad state", s);
    }

    [Fact]
    public void FormatToolInvocationFailure_long_message_truncated()
    {
        var longMsg = new string('x', ToolInvocationFailureFormatter.MaxDetailChars + 100);
        var ex = new ArgumentException(longMsg);
        var s = ToolInvocationFailureFormatter.FormatToolInvocationFailure("P", "f", ex);
        Assert.True(s.Length < longMsg.Length + 80);
        Assert.EndsWith("…", s);
    }

    [Fact]
    public void ShouldRethrowAsCancellation_true_when_token_cancelled_and_operation_canceled()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var ex = new OperationCanceledException(cts.Token);
        Assert.True(ToolInvocationFailureFormatter.ShouldRethrowAsCancellation(ex, cts.Token));
    }

    [Fact]
    public void ShouldRethrowAsCancellation_false_when_not_cancelled()
    {
        var ex = new OperationCanceledException();
        Assert.False(ToolInvocationFailureFormatter.ShouldRethrowAsCancellation(ex, CancellationToken.None));
    }

    [Fact]
    public void ShouldRethrowAsCancellation_false_for_non_cancel_exception()
    {
        Assert.False(ToolInvocationFailureFormatter.ShouldRethrowAsCancellation(new ArgumentException(), new CancellationToken(true)));
    }
}

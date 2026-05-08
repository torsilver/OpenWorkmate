namespace OpenWorkmate.Server.Services.Maf;

/// <summary>主会话 MAF/SK 流式共享：上下文超长时由运行器置位，外层循环重建 <c>turn.HistoryToUse</c> 后重试。</summary>
public sealed class StreamPassOutcome
{
    public bool ContextLengthRetryRequested { get; set; }
}

using Microsoft.Extensions.AI;
using OpenWorkmate.Server.Services;

namespace OpenWorkmate.Server.Services.Maf;

/// <summary>上下文长度错误检测与为重试裁剪历史（实现委托 <see cref="ContextManager.TrimHistoryForRetry"/>，与主路径裁剪边界一致）。</summary>
public static class ContextLengthRetryHelper
{
    public static bool IsContextLengthError(Exception ex) => ContextManager.IsContextLengthError(ex);

    /// <summary>为 context_length 重试裁剪历史：先按轮数限制，再按预算减半裁剪。</summary>
    public static void TrimHistoryForRetry(List<ChatMessage> history, int maxTurns, ContextWindowConfig ctx) =>
        ContextManager.TrimHistoryForRetry(history, maxTurns, ctx);
}

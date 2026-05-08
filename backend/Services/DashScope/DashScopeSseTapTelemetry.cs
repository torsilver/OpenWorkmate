namespace OpenWorkmate.Server.Services.DashScope;

/// <summary>单次 chat/completions 流式响应的 SSE 旁路统计，供排查 reasoning_content 未进 WS 时使用。</summary>
internal sealed class DashScopeSseTapTelemetry
{
    /// <summary>前若干条 <c>data:</c> 行 JSON 截断预览（与百炼对账用；不含完整流）。</summary>
    public List<string> SsePayloadPreviews { get; } = new();

    public int SseDataLines { get; set; }
    /// <summary>JSON 中含非空 choices 数组的 data 行数。</summary>
    public int ChoiceChunksSeen { get; set; }
    /// <summary>解析器认为应下发的推理字符串片段次数（调用 emit 的次数）。</summary>
    public int ReasoningFragmentsParsed { get; set; }
    public int JsonParseErrors { get; set; }
    /// <summary><see cref="DashScopeReasoningContext"/> 无当前帧时丢弃的片段数（AsyncLocal/时序问题信号）。</summary>
    public int EnqueueDroppedNoAsyncLocalFrame { get; set; }
}

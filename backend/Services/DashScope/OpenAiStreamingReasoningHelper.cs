using Microsoft.SemanticKernel;

namespace OfficeCopilot.Server.Services.DashScope;

/// <summary>从 SK 流式 chunk 的 Metadata 中尝试取出推理增量（若连接器未来映射百炼字段）。</summary>
internal static class OpenAiStreamingReasoningHelper
{
    private static readonly string[] MetadataKeys = { "reasoning_content", "ReasoningContent", "reasoning" };

    internal static string? TryGetReasoningFromMetadata(StreamingChatMessageContent chunk)
    {
        var md = chunk.Metadata;
        if (md is null || md.Count == 0)
            return null;
        foreach (var key in MetadataKeys)
        {
            if (!md.TryGetValue(key, out var o) || o == null)
                continue;
            if (o is string s && s.Length > 0)
                return s;
        }

        return null;
    }
}

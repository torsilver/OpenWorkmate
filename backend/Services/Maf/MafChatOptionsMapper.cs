using Microsoft.Extensions.AI;

namespace OpenWorkmate.Server.Services.Maf;

/// <summary>将基础 <see cref="ChatOptions"/> 与本轮工具列表合并为 MAF <see cref="ChatClientAgent"/> 使用的选项（新实例，不修改传入的 base）。</summary>
public static class MafChatOptionsMapper
{
    /// <param name="requireToolInvocation">为 true 时要求至少一次工具调用（对齐原 SK <c>FunctionChoiceBehavior.Required</c> 的常见意图）。</param>
    public static ChatOptions ToChatOptions(ChatOptions? baseOptions, IList<AITool>? tools, bool requireToolInvocation = false)
    {
        var o = new ChatOptions();
        if (baseOptions != null)
        {
            o.Temperature = baseOptions.Temperature;
            o.TopP = baseOptions.TopP;
            o.FrequencyPenalty = baseOptions.FrequencyPenalty;
            o.PresencePenalty = baseOptions.PresencePenalty;
            o.MaxOutputTokens = baseOptions.MaxOutputTokens;
            o.Instructions = baseOptions.Instructions;
            CopyStopSequences(baseOptions, o);
        }

        if (tools is { Count: > 0 })
            o.Tools = tools;
        if (requireToolInvocation && tools is { Count: > 0 })
            o.ToolMode = ChatToolMode.RequireAny;
        return o;
    }

    private static void CopyStopSequences(ChatOptions from, ChatOptions to)
    {
        var stops = from.StopSequences;
        if (stops is null) return;
        if (stops is string[] arr)
        {
            if (arr.Length > 0)
                to.StopSequences = (string[])arr.Clone();
            return;
        }

        var count = stops.Count;
        if (count <= 0) return;
        var copy = new string[count];
        for (var i = 0; i < count; i++)
            copy[i] = stops[i];
        to.StopSequences = copy;
    }
}

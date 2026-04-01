using System.Collections;
using System.Globalization;
using Microsoft.SemanticKernel;

namespace OfficeCopilot.Server.Services.SemanticKernel;

/// <summary>从 <see cref="StreamingChatMessageContent.Items"/> 提取 <see cref="StreamingFunctionCallUpdateContent"/> 并做单 call 累计长度限制。</summary>
public static class StreamingToolCallDeltaHelper
{
    public const int MaxArgumentsCumulativeCharsPerCall = 32 * 1024;

    /// <summary>供单元测试：遍历任意 item 集合（与 SK <see cref="StreamingChatMessageContent.Items"/> 枚举方式一致）。</summary>
    public static IEnumerable<ToolCallStreamDelta> ExtractFromItems(
        IEnumerable? items,
        Dictionary<string, int> cumulativeArgumentsLengthByCallKey)
    {
        if (items == null) yield break;
        foreach (var item in items)
        {
            if (item is not StreamingFunctionCallUpdateContent u)
                continue;

            var key = !string.IsNullOrEmpty(u.CallId)
                ? u.CallId
                : "i" + u.FunctionCallIndex.ToString(CultureInfo.InvariantCulture);

            var name = u.Name;
            var argDelta = u.Arguments ?? "";
            if (string.IsNullOrEmpty(name) && string.IsNullOrEmpty(argDelta))
                continue;

            if (!string.IsNullOrEmpty(argDelta))
            {
                var prev = cumulativeArgumentsLengthByCallKey.GetValueOrDefault(key);
                if (prev >= MaxArgumentsCumulativeCharsPerCall)
                    continue;
                var allowed = MaxArgumentsCumulativeCharsPerCall - prev;
                var take = Math.Min(argDelta.Length, allowed);
                if (take < argDelta.Length)
                    argDelta = argDelta.Substring(0, take);
                cumulativeArgumentsLengthByCallKey[key] = prev + take;
            }

            var callIdOut = string.IsNullOrEmpty(u.CallId) ? key : u.CallId;
            yield return new ToolCallStreamDelta(callIdOut, string.IsNullOrEmpty(name) ? null : name, argDelta);
        }
    }

    public static IEnumerable<ToolCallStreamDelta> ExtractFromChunk(
        StreamingChatMessageContent chunk,
        Dictionary<string, int> cumulativeArgumentsLengthByCallKey) =>
        ExtractFromItems(chunk.Items, cumulativeArgumentsLengthByCallKey);
}

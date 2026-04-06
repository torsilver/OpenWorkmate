using System.Globalization;
using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using OfficeCopilot.Server.Services.Chat;

namespace OfficeCopilot.Server.Services.Maf;

/// <summary>从 MAF 流式 <see cref="AgentResponseUpdate"/> 提取 <see cref="ToolCallStreamDelta"/>（与 SK StreamingToolCallDeltaHelper 行为对齐的简化版：按 callId 累计参数字符串并输出增量）。</summary>
public static class MafToolCallDeltaExtractor
{
    public static IEnumerable<ToolCallStreamDelta> ExtractFromAgentResponseUpdate(
        AgentResponseUpdate update,
        Dictionary<string, int> cumulativeArgumentsLengthByCallKey,
        Dictionary<string, (string Name, string ArgsSoFar)> callState)
    {
        ChatResponseUpdate cru;
        try
        {
            cru = update.AsChatResponseUpdate();
        }
        catch
        {
            yield break;
        }

        foreach (var content in cru.Contents ?? [])
        {
            if (content is not FunctionCallContent fcc)
                continue;

            var callId = string.IsNullOrEmpty(((ToolCallContent)fcc).CallId) ? "call" : ((ToolCallContent)fcc).CallId;
            var name = fcc.Name ?? "";
            var argsText = SerializeArguments(fcc.Arguments);
            callState.TryGetValue(callId, out var prev);
            var prevArgs = prev.ArgsSoFar ?? "";
            string delta;
            if (argsText.Length >= prevArgs.Length && argsText.StartsWith(prevArgs, StringComparison.Ordinal))
                delta = argsText.Substring(prevArgs.Length);
            else
                delta = argsText;
            callState[callId] = (name, argsText);

            if (string.IsNullOrEmpty(name) && string.IsNullOrEmpty(delta))
                continue;

            if (!string.IsNullOrEmpty(delta))
            {
                var key = callId;
                var prevLen = cumulativeArgumentsLengthByCallKey.GetValueOrDefault(key);
                if (prevLen >= StreamingToolCallDeltaHelper.MaxArgumentsCumulativeCharsPerCall)
                    continue;
                var allowed = StreamingToolCallDeltaHelper.MaxArgumentsCumulativeCharsPerCall - prevLen;
                var take = Math.Min(delta.Length, allowed);
                if (take < delta.Length)
                    delta = delta.Substring(0, take);
                cumulativeArgumentsLengthByCallKey[key] = prevLen + take;
            }

            yield return new ToolCallStreamDelta(callId, string.IsNullOrEmpty(name) ? null : name, delta);
        }
    }

    private static string SerializeArguments(IDictionary<string, object?>? arguments)
    {
        if (arguments == null || arguments.Count == 0)
            return "";
        try
        {
            return JsonSerializer.Serialize(arguments);
        }
        catch
        {
            return string.Join(",", arguments.Select(kv => kv.Key + "=" + Convert.ToString(kv.Value, CultureInfo.InvariantCulture)));
        }
    }
}

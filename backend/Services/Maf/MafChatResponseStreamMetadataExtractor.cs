using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace OfficeCopilot.Server.Services.Maf;

/// <summary>跨 MAF 流式 <c>AgentResponseUpdate</c> 调用保持去重状态（finish / role / meta 各最多一条逻辑序列）。</summary>
public sealed class MafStreamDeltaMetadataState
{
    public ChatFinishReason? LastFinishEmitted;
    public bool RoleEmitted;
    public bool MetaEmitted;
}

/// <summary>从 MEAI <see cref="ChatResponseUpdate"/> 抽取与 OpenAI 流式 chunk 对齐的元数据（用量由 SSE 旁路 <c>OpenAiStreamUsageSessionBridge</c> 提供）。</summary>
public static class MafChatResponseStreamMetadataExtractor
{
    public static IEnumerable<StreamItem> ExtractFromAgentUpdate(AgentResponseUpdate update, MafStreamDeltaMetadataState state)
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

        if (cru.FinishReason is { } fr)
        {
            if (state.LastFinishEmitted != fr)
            {
                state.LastFinishEmitted = fr;
                yield return new StreamItem(IsWarning: false, Content: fr.ToString(), Kind: StreamSegmentKind.StreamFinish);
            }
        }

        if (!state.RoleEmitted && cru.Role is { } role)
        {
            state.RoleEmitted = true;
            yield return new StreamItem(IsWarning: false, Content: role.ToString(), Kind: StreamSegmentKind.StreamRole);
        }

        if (!state.MetaEmitted && (!string.IsNullOrEmpty(cru.ResponseId) || !string.IsNullOrEmpty(cru.ModelId)))
        {
            state.MetaEmitted = true;
            var json = BuildMetaJson(cru);
            if (!string.IsNullOrEmpty(json))
                yield return new StreamItem(IsWarning: false, Content: json, Kind: StreamSegmentKind.StreamMeta);
        }
    }

    private static string BuildMetaJson(ChatResponseUpdate cru)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            if (!string.IsNullOrEmpty(cru.ResponseId))
                w.WriteString("responseId", cru.ResponseId);
            if (!string.IsNullOrEmpty(cru.ModelId))
                w.WriteString("modelId", cru.ModelId);
            if (cru.CreatedAt is { } ca)
                w.WriteString("createdAt", ca.ToString("O"));
            w.WriteEndObject();
        }

        return System.Text.Encoding.UTF8.GetString(ms.ToArray());
    }
}

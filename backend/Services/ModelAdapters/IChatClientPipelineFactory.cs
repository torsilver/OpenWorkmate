using Microsoft.Extensions.AI;
using OfficeCopilot.Server;

namespace OfficeCopilot.Server.Services.ModelAdapters;

/// <summary>为单条 <see cref="AiModelEntry"/> 构建主对话用 <see cref="IChatClient"/>（OpenAI SDK + MEAI 包装 + HTTP 管道）。</summary>
public interface IChatClientPipelineFactory
{
    /// <param name="entry">模型配置条目（用于百炼 Handler 内解析扩展字段等）。</param>
    /// <param name="resolvedModelId">已处理 Azure deployment 后的模型 id。</param>
    /// <param name="endpointUri">解析后的 API 基址；网关模式下为重写后的 relay 基址。</param>
    IChatClient CreateChatClient(AiModelEntry entry, string resolvedModelId, Uri? endpointUri, string apiKey);
}

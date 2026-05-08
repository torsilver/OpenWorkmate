using System.Text.Json;

namespace OpenWorkmate.Server.Mcp;

/// <summary>
/// MEAI 10.4 的 <c>AIFunctionFactoryOptions</c> 无法挂载 MCP <c>inputSchema</c>，将 schema 以受控长度附加到工具 Description，供模型填参参考。
/// </summary>
internal static class McpToolSchemaDescriptionFormatter
{
    internal const int MaxSchemaAppendChars = 8000;

    private static readonly JsonSerializerOptions CompactUtf8 = new()
    {
        WriteIndented = false,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    /// <summary>将 MCP 声明的 inputSchema 附加到描述末尾；无有效 schema 时返回原描述。</summary>
    internal static string CombineDescriptionWithInputSchema(string? description, JsonElement inputSchema)
    {
        var baseDesc = (description ?? "").Trim();
        if (inputSchema.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
            return baseDesc;

        if (inputSchema.ValueKind == JsonValueKind.Object)
        {
            var hasAny = false;
            foreach (var _ in inputSchema.EnumerateObject())
            {
                hasAny = true;
                break;
            }

            if (!hasAny)
                return baseDesc;
        }

        var schemaText = JsonSerializer.Serialize(inputSchema, CompactUtf8);
        if (schemaText.Length > MaxSchemaAppendChars)
            schemaText = schemaText[..MaxSchemaAppendChars] + "\n…[inputSchema 已截断]";

        var appendix = "\n\n【MCP inputSchema】填参请与此 JSON Schema 一致（与运行时校验一致）：\n" + schemaText;
        if (baseDesc.Length == 0)
            return appendix.TrimStart();
        return baseDesc + appendix;
    }
}

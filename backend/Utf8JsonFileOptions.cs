using System.Text.Encodings.Web;
using System.Text.Json;

namespace OfficeCopilot.Server;

/// <summary>
/// 本机 JSON 落盘用序列化选项：<see cref="System.Text.Json"/> 默认会把非 ASCII 写成 <c>\uXXXX</c>，
/// 用 <see cref="JavaScriptEncoder.UnsafeRelaxedJsonEscaping"/> 后 UTF-8 文件中为可读字面量（与 <see cref="ConfigService"/> 保存配置一致）。
/// </summary>
public static class Utf8JsonFileOptions
{
    public static JsonSerializerOptions Compact { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static JsonSerializerOptions Indented { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };
}

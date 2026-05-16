using System.Diagnostics.CodeAnalysis;
using System.Text.Json;

namespace OpenWorkmate.Server.Plugins;

/// <summary>
/// 将模型传入的 <c>paragraphs</c>（数组、单字符串、或罕见形态）规范为 <c>string[]?</c>，避免 MEAI 对 <c>string[]</c> 的严格 JSON 绑定在根路径失败。
/// </summary>
internal static class WordDocumentCreateParagraphsParser
{
    public static string[]? Parse(JsonElement? paragraphs)
    {
        if (paragraphs is null)
            return null;
        return Parse(paragraphs.Value);
    }

    public static string[]? Parse(JsonElement e)
    {
        switch (e.ValueKind)
        {
            case JsonValueKind.Null:
            case JsonValueKind.Undefined:
                return null;
            case JsonValueKind.Array:
            {
                var raw = e.EnumerateArray().Select(ElementToParagraphString).ToArray();
                // 常见误用：整段 ["…","…"] 被塞进数组的**单个**元素；或「标题串 + 一整段 JSON 数组字面量」共两项。
                // 对每一项尝试展开，避免仅 length==1 时才展开导致第二项原样落盘。
                var flat = new List<string>(raw.Length + 4);
                foreach (var seg in raw)
                {
                    if (TryExpandEmbeddedJsonStringArray(seg, out var expanded))
                        flat.AddRange(expanded);
                    else
                        flat.Add(seg);
                }

                return flat.Count == 0 ? Array.Empty<string>() : flat.ToArray();
            }
            case JsonValueKind.String:
            {
                var s = e.GetString() ?? "";
                if (TryExpandEmbeddedJsonStringArray(s, out var expanded))
                    return expanded;
                return new[] { s };
            }
            case JsonValueKind.Object:
                // 少数模型会输出 { "0": "…", "1": "…" } 或单键对象
                var list = new List<string>();
                foreach (var p in e.EnumerateObject().OrderBy(x => x.Name, StringComparer.Ordinal))
                {
                    if (p.Value.ValueKind == JsonValueKind.String)
                        list.Add(p.Value.GetString() ?? "");
                    else
                        list.Add(p.Value.GetRawText());
                }

                return list.Count == 0 ? Array.Empty<string>() : list.ToArray();
            default:
                return new[] { e.GetRawText() };
        }
    }

    private static string ElementToParagraphString(JsonElement x)
    {
        if (x.ValueKind == JsonValueKind.String)
            return x.GetString() ?? "";
        return x.GetRawText();
    }

    /// <summary>
    /// 若 <paramref name="s"/> 整段为合法 JSON 字符串数组（模型常把数组序列化后再塞进 string 字段），展开为多条段落。
    /// </summary>
    private static bool TryExpandEmbeddedJsonStringArray(string s, [NotNullWhen(true)] out string[]? expanded)
    {
        expanded = null;
        var t = s.Trim();
        if (t.Length < 3 || t[0] != '[')
            return false;
        try
        {
            using var doc = JsonDocument.Parse(t);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
                return false;
            var list = new List<string>();
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.String)
                    return false;
                list.Add(item.GetString() ?? "");
            }

            if (list.Count == 0)
                return false;
            expanded = list.ToArray();
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}

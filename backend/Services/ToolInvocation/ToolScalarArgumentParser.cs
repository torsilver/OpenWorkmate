using System.Globalization;
using System.Text.Json;

namespace OpenWorkmate.Server.Services.ToolInvocation;

/// <summary>
/// 将模型工具参数中的 <see cref="JsonElement"/> 宽松解析为标量，避免 JSON 字符串与布尔/数字类型不一致导致 MEAI 反射绑定期 <see cref="JsonException"/>。
/// </summary>
public static class ToolScalarArgumentParser
{
    /// <summary>未传参（省略）或 JSON null：用于可选 bool?。</summary>
    public static bool IsUnspecifiedOrNull(JsonElement el) =>
        el.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null;

    /// <summary>
    /// 工具参数用 <see cref="JsonElement?"/>（默认 <c>null</c>）以便 MEAI 生成 JSON Schema 时不序列化无效的 <c>default(JsonElement)</c>（Undefined）。
    /// </summary>
    public static bool IsOmitted(JsonElement? el) =>
        !el.HasValue || IsUnspecifiedOrNull(el.Value);

    /// <summary>
    /// 解析布尔：JSON true/false、字符串 "true"/"false"/"1"/"0"/yes/no（大小写不敏感）。
    /// </summary>
    public static bool TryReadBool(JsonElement el, out bool value)
    {
        value = default;
        switch (el.ValueKind)
        {
            case JsonValueKind.True:
                value = true;
                return true;
            case JsonValueKind.False:
                value = false;
                return true;
            case JsonValueKind.String:
                return TryParseBoolString(el.GetString(), out value);
            case JsonValueKind.Number:
                if (el.TryGetInt64(out var n))
                {
                    if (n == 1) { value = true; return true; }
                    if (n == 0) { value = false; return true; }
                }

                return false;
            default:
                return false;
        }
    }

    /// <summary>
    /// 省略或未传 → <paramref name="defaultIfUnspecified"/>；否则按 <see cref="TryReadBool"/>。
    /// </summary>
    public static bool TryReadBoolWithDefault(JsonElement el, bool defaultIfUnspecified, out bool value)
    {
        if (IsUnspecifiedOrNull(el))
        {
            value = defaultIfUnspecified;
            return true;
        }

        return TryReadBool(el, out value);
    }

    /// <inheritdoc cref="TryReadBoolWithDefault(JsonElement,bool,out bool)"/>
    public static bool TryReadBoolWithDefault(JsonElement? el, bool defaultIfUnspecified, out bool value)
    {
        if (IsOmitted(el))
        {
            value = defaultIfUnspecified;
            return true;
        }

        return TryReadBool(el!.Value, out value);
    }

    /// <summary>
    /// 用于 <c>bool?</c> 工具参数：<c>Undefined</c> → <paramref name="wasSpecified"/> false；
    /// JSON <c>null</c> → <paramref name="wasSpecified"/> true 且 <paramref name="value"/> null（显式空）；
    /// 否则解析为 true/false。
    /// </summary>
    public static bool TryReadNullableBool(JsonElement el, out bool? value, out bool wasSpecified)
    {
        value = null;
        wasSpecified = false;
        if (el.ValueKind == JsonValueKind.Undefined)
            return true;
        if (el.ValueKind == JsonValueKind.Null)
        {
            wasSpecified = true;
            value = null;
            return true;
        }

        if (!TryReadBool(el, out var b))
            return false;
        wasSpecified = true;
        value = b;
        return true;
    }

    /// <inheritdoc cref="TryReadNullableBool(JsonElement,out bool?,out bool)"/>
    public static bool TryReadNullableBool(JsonElement? el, out bool? value, out bool wasSpecified)
    {
        value = null;
        wasSpecified = false;
        if (!el.HasValue)
            return true;
        return TryReadNullableBool(el.Value, out value, out wasSpecified);
    }

    public static bool TryParseBoolString(string? s, out bool value)
    {
        value = default;
        if (string.IsNullOrWhiteSpace(s))
            return false;
        var t = s.Trim();
        if (bool.TryParse(t, out value))
            return true;
        if (t.Equals("1", StringComparison.OrdinalIgnoreCase) || t.Equals("yes", StringComparison.OrdinalIgnoreCase))
        {
            value = true;
            return true;
        }

        if (t.Equals("0", StringComparison.OrdinalIgnoreCase) || t.Equals("no", StringComparison.OrdinalIgnoreCase))
        {
            value = false;
            return true;
        }

        return false;
    }

    public static bool TryReadInt32(JsonElement el, out int value)
    {
        value = default;
        switch (el.ValueKind)
        {
            case JsonValueKind.Number:
                return el.TryGetInt32(out value);
            case JsonValueKind.String:
                return int.TryParse(el.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
            default:
                return false;
        }
    }

    /// <summary>省略或 JSON null → <paramref name="defaultIfUnspecified"/>；否则按 <see cref="TryReadInt32"/>。</summary>
    public static bool TryReadInt32WithDefault(JsonElement el, int defaultIfUnspecified, out int value)
    {
        if (IsUnspecifiedOrNull(el))
        {
            value = defaultIfUnspecified;
            return true;
        }

        return TryReadInt32(el, out value);
    }

    /// <inheritdoc cref="TryReadInt32WithDefault(JsonElement,int,out int)"/>
    public static bool TryReadInt32WithDefault(JsonElement? el, int defaultIfUnspecified, out int value)
    {
        if (IsOmitted(el))
        {
            value = defaultIfUnspecified;
            return true;
        }

        return TryReadInt32(el!.Value, out value);
    }

    public static bool TryReadInt64(JsonElement el, out long value)
    {
        value = default;
        switch (el.ValueKind)
        {
            case JsonValueKind.Number:
                return el.TryGetInt64(out value);
            case JsonValueKind.String:
                return long.TryParse(el.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
            default:
                return false;
        }
    }

    public static bool TryReadDouble(JsonElement el, out double value)
    {
        value = default;
        switch (el.ValueKind)
        {
            case JsonValueKind.Number:
                return el.TryGetDouble(out value);
            case JsonValueKind.String:
                return double.TryParse(el.GetString(), NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out value);
            default:
                return false;
        }
    }

    /// <summary>省略或 null → false（未提供）；否则解析 int。</summary>
    public static bool TryReadNullableInt32(JsonElement el, out int? value, out bool wasSpecified)
    {
        value = null;
        wasSpecified = false;
        if (el.ValueKind == JsonValueKind.Undefined)
            return true;
        if (el.ValueKind == JsonValueKind.Null)
        {
            wasSpecified = true;
            value = null;
            return true;
        }

        if (!TryReadInt32(el, out var i))
            return false;
        wasSpecified = true;
        value = i;
        return true;
    }

    /// <inheritdoc cref="TryReadNullableInt32(JsonElement,out int?,out bool)"/>
    public static bool TryReadNullableInt32(JsonElement? el, out int? value, out bool wasSpecified)
    {
        value = null;
        wasSpecified = false;
        if (!el.HasValue)
            return true;
        return TryReadNullableInt32(el.Value, out value, out wasSpecified);
    }

    /// <summary>省略或 null → false（未提供）；否则解析 long。</summary>
    public static bool TryReadNullableInt64(JsonElement el, out long? value, out bool wasSpecified)
    {
        value = null;
        wasSpecified = false;
        if (el.ValueKind == JsonValueKind.Undefined)
            return true;
        if (el.ValueKind == JsonValueKind.Null)
        {
            wasSpecified = true;
            value = null;
            return true;
        }

        if (!TryReadInt64(el, out var l))
            return false;
        wasSpecified = true;
        value = l;
        return true;
    }

    /// <summary>省略或 null → false（未提供）；否则解析 double。</summary>
    public static bool TryReadNullableDouble(JsonElement el, out double? value, out bool wasSpecified)
    {
        value = null;
        wasSpecified = false;
        if (el.ValueKind == JsonValueKind.Undefined)
            return true;
        if (el.ValueKind == JsonValueKind.Null)
        {
            wasSpecified = true;
            value = null;
            return true;
        }

        if (!TryReadDouble(el, out var d))
            return false;
        wasSpecified = true;
        value = d;
        return true;
    }
}

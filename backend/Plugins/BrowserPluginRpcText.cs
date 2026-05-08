using System.Text.Json;

namespace OpenWorkmate.Server.Plugins;

/// <summary>Browser 插件 RPC 返回值解析与「空结果」用户可读说明（无外部依赖，可单测）。</summary>
public static class BrowserPluginRpcText
{
    public static string? TryParseResultString(JsonElement? result)
    {
        if (result is not { } el)
            return null;
        return el.ValueKind switch
        {
            JsonValueKind.Null or JsonValueKind.Undefined => null,
            JsonValueKind.String => el.GetString(),
            _ => el.ToString()
        };
    }

    public static bool IsEffectivelyEmpty(string? s) => string.IsNullOrWhiteSpace(s);

    public static string PageScriptEmptyNotice(JsonValueKind kindWhenPresent) =>
        "页面脚本已执行，但返回内容为空（模型侧看不到正文）。"
        + "可能原因：脚本在页面上取到的文本为空、选择器未命中、或 lazy 内容尚未进入 DOM。"
        + "可尝试先 scroll_to_bottom / wait_for_selector，或使用 get_visible_text 且 truncateMode 为 tail。"
        + $"（rpc result 类型：{kindWhenPresent}）";

    public static string CustomScriptEmptyNotice(JsonValueKind kindWhenPresent) =>
        "自定义页面脚本已执行，但返回内容为空。"
        + "请确认代码在页面上下文中以 return 返回字符串或可 JSON 序列化的值；若仅执行语句而无 return，扩展侧会得到空字符串。"
        + "若 DOM 无匹配也会为空。"
        + $"（rpc result 类型：{kindWhenPresent}）";
}

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
        + "可尝试 page_agent observe 后针对 ref 用 waitFor/scrollIntoView，或用 run_custom_javascript_in_page 滚动并 return 抽取的文本。"
        + $"（rpc result 类型：{kindWhenPresent}）";

    public static string CustomScriptEmptyNotice(JsonValueKind kindWhenPresent) =>
        "自定义页面脚本已执行，但返回内容为空。"
        + "请确认代码在页面上下文中以 return 返回字符串或可 JSON 序列化的值；若仅执行语句而无 return，扩展侧会得到空字符串。"
        + "若 DOM 无匹配也会为空。"
        + $"（rpc result 类型：{kindWhenPresent}）";

    public static string PageAgentEmptyNotice(JsonValueKind kindWhenPresent) =>
        "page_agent 已调用，但扩展返回内容为空。请确认 Chrome 侧栏已连接、当前标签页可注入脚本。"
        + $"（rpc result 类型：{kindWhenPresent}）";
}

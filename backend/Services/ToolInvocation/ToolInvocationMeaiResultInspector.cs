using Microsoft.Extensions.AI;

namespace OfficeCopilot.Server.Services.ToolInvocation;

/// <summary>
/// Microsoft.Extensions.AI <see cref="FunctionInvokingChatClient"/> 在工具内异常时可能不向外抛错，
/// 而以 <see cref="FunctionInvokingChatClient.FunctionInvocationResult"/> 封装 Status/Exception；
/// 与「仅 try/catch」的 <see cref="ToolInvocationMiddleware"/> 配合时，需在此解包后再做 ToolStatus / 语义失败判定。
/// </summary>
public static class ToolInvocationMeaiResultInspector
{
    /// <summary>若 <paramref name="nextResult"/> 为 MEAI 封装类型则取出内层返回值，否则原样返回。</summary>
    public static object? GetEffectivePayload(object? nextResult) =>
        nextResult is FunctionInvokingChatClient.FunctionInvocationResult fr ? fr.Result : nextResult;

    /// <summary>
    /// 封装体自身是否报告失败（与 <see cref="ToolSemanticFailureMarkers"/> 对内层 Result 文本的判定互补）。
    /// </summary>
    public static bool TryGetEnvelopeFailureMessage(
        object? nextResult,
        string pluginName,
        string functionName,
        out string failureMessage)
    {
        failureMessage = "";
        if (nextResult is not FunctionInvokingChatClient.FunctionInvocationResult fir)
            return false;

        if (fir.Status == FunctionInvokingChatClient.FunctionInvocationStatus.RanToCompletion && fir.Exception is null)
            return false;

        if (fir.Exception is not null)
        {
            failureMessage = ToolInvocationFailureFormatter.FormatToolInvocationFailure(pluginName, functionName, fir.Exception);
            return true;
        }

        failureMessage = fir.Status switch
        {
            FunctionInvokingChatClient.FunctionInvocationStatus.NotFound =>
                $"[工具调用失败] {pluginName}.{functionName}: 未找到模型请求的工具。",
            FunctionInvokingChatClient.FunctionInvocationStatus.Exception =>
                $"[工具调用失败] {pluginName}.{functionName}: 工具执行失败（无异常详情）。",
            _ => $"[工具调用失败] {pluginName}.{functionName}: 状态 {fir.Status}。"
        };
        return true;
    }
}

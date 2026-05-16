using System.ComponentModel;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using OpenWorkmate.Server;
using OpenWorkmate.Server.Logging;
using OpenWorkmate.Server.Services;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace OpenWorkmate.Server.Plugins;

[OpenWorkmatePluginId("Browser")]
public class BrowserPlugin
{
    private readonly SessionManager _sessionManager;
    private readonly RpcManager _rpcManager;
    private readonly ScreenshotCacheService _screenshotCache;
    private readonly ILogger<BrowserPlugin> _logger;

    public BrowserPlugin(SessionManager sessionManager, RpcManager rpcManager, ScreenshotCacheService screenshotCache, ILogger<BrowserPlugin> logger)
    {
        _sessionManager = sessionManager;
        _rpcManager = rpcManager;
        _screenshotCache = screenshotCache;
        _logger = logger;
    }

    [ToolFunction("highlight_webpage_text")]
    [Description("Highlights specific text on the user's current active webpage.")]
    public async Task<string> HighlightTextAsync(
        [Description("The exact text or keyword to highlight on the webpage")] string text,
        [Description("The CSS color to use for highlighting (e.g., 'yellow', '#ff0000'). Default is yellow.")] string color = "yellow")
    {
        var sessionId = SessionContext.GetSessionId();
        _logger.LogInformation("[Browser] highlight_webpage_text textLen={Len} textPreview={Text} color={Color} sessionId={SessionId}",
            text?.Length ?? 0, LogPreview.HeadTail(text, 40, 40), color, sessionId ?? "(null)");
        if (string.IsNullOrEmpty(sessionId))
            return "Error: Session ID is missing (no active chat context).";

        var ws = _sessionManager.Get(sessionId);
        if (ws == null)
        {
            _logger.LogWarning("[Browser] No WebSocket for sessionId={SessionId}", sessionId);
            return "Error: WebSocket connection not found for this session.";
        }

        var reqId = _rpcManager.RegisterRequest(out var responseTask);

        var msg = new WsMessage
        {
            Type = "rpc_request",
            Id = reqId,
            Method = "highlight_text",
            Params = JsonSerializer.SerializeToElement(new { text, color })
        };

        var payload = JsonSerializer.Serialize(msg, JsonCtx.Default.WsMessage);
        await _sessionManager.SendToAsync(sessionId, payload);

        try
        {
            var result = await responseTask;
            var resultMsg = result?.ToString() ?? "成功：高亮指令已发送并在页面上执行。";
            _logger.LogInformation("[Browser] highlight_webpage_text reqId={ReqId} resultLen={Len} resultPreview={Result}",
                reqId, resultMsg.Length, LogPreview.HeadTail(resultMsg, 120, 120));
            return resultMsg;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Browser] highlight_webpage_text reqId={ReqId} failed", reqId);
            return "失败：浏览器高亮执行出错（" + ex.Message + "）。请确认当前标签页允许扩展注入脚本。";
        }
    }

    [ToolFunction("add_floating_note")]
    [Description("Adds a floating sticky note on the user's current webpage. Content and title are fully customizable. Multiple notes can exist at once. Optionally anchor the note above a specific text snippet.")]
    public async Task<string> AddFloatingNoteAsync(
        [Description("The main content of the floating note (AI can write any explanation, translation, or tip here)")] string message,
        [Description("Optional. Custom title shown in the note header (e.g. '翻译', '解释'). Default is 'Open Workmate'.")] string? title = null,
        [Description("Optional. If provided, the note will be positioned above the first occurrence of this text on the page; otherwise shown at top-right corner.")] string? anchorText = null)
    {
        var sessionId = SessionContext.GetSessionId();
        _logger.LogInformation("[Browser] add_floating_note sessionId={SessionId} anchorLen={AnchorLen} anchorPreview={Anchor}",
            sessionId ?? "(null)", anchorText?.Length ?? 0, LogPreview.HeadTail(anchorText ?? "(default)", 40, 40));
        if (string.IsNullOrEmpty(sessionId))
            return "Error: Session ID is missing (no active chat context).";

        var ws = _sessionManager.Get(sessionId);
        if (ws == null)
            return "Error: WebSocket connection not found for this session.";

        var reqId = _rpcManager.RegisterRequest(out var responseTask);

        var msg = new WsMessage
        {
            Type = "rpc_request",
            Id = reqId,
            Method = "add_floating_note",
            Params = JsonSerializer.SerializeToElement(new { message, title, anchorText })
        };

        var payload = JsonSerializer.Serialize(msg, JsonCtx.Default.WsMessage);
        await _sessionManager.SendToAsync(sessionId, payload);

        try
        {
            var result = await responseTask;
            return result?.ToString() ?? "成功：悬浮笔记已添加到页面。";
        }
        catch (Exception ex)
        {
            return "失败：添加悬浮笔记时出错（" + ex.Message + "）。";
        }
    }

    /// <summary>Chrome 当前活动标签页：页内基座 SDK（observe/ref + 少量动作），返回 JSON 字符串。</summary>
    [ToolFunction("page_agent")]
    [Description(
        "【Chrome / 页内基座】单一 JSON 字符串参数 requestJson，由扩展注入 <c>page-agent-sdk.js</c> 执行。仅作用于当前活动标签页；用户需先切到目标页。\n" +
        "定位：面向「操作页面」——observe 扫描**可交互**控件并分配 ref，返回 tag/role/短 name 等元数据；**不是**文章正文、不是整页可读长文。用户要总结/摘录博客或新闻**正文**时，勿指望仅靠 observe，应改用 run_custom_javascript_in_page。\n" +
        "op：observe | click{ref} | fill{ref,value} | waitFor{ref,timeoutMs?} | scrollIntoView{ref}。应先 observe 再对 ref 执行动作。\n" +
        "返回为 JSON 字符串（ok/error）；错误码如 BAD_REQUEST、NOT_FOUND、TIMEOUT、STALE_REF（页面导航后须重新 observe）。\n" +
        "读全文或复杂 DOM 抽取用 run_custom_javascript_in_page。")]
    public async Task<string> PageAgentAsync(
        [Description("JSON 对象字符串，如 {\"op\":\"observe\"} 或 {\"op\":\"click\",\"ref\":\"r0\"}。")] string requestJson)
    {
        const int MaxRequestLen = 32 * 1024;
        if (string.IsNullOrWhiteSpace(requestJson))
            return "失败：requestJson 不能为空。";
        if (requestJson.Length > MaxRequestLen)
            return $"失败：requestJson 超过最大长度（{MaxRequestLen} 字符）。";

        var sessionId = SessionContext.GetSessionId();
        _logger.LogInformation("[Browser] page_agent requestLen={Len} sessionId={SessionId}", requestJson.Length, sessionId ?? "(null)");
        if (string.IsNullOrEmpty(sessionId))
            return "Error: Session ID is missing (no active chat context).";

        if (_sessionManager.Get(sessionId) == null)
            return "Error: WebSocket connection not found for this session.";

        var reqId = _rpcManager.RegisterRequest(out var responseTask);
        var msg = new WsMessage
        {
            Type = "rpc_request",
            Id = reqId,
            Method = "page_agent",
            Params = JsonSerializer.SerializeToElement(new { requestJson })
        };
        var payload = JsonSerializer.Serialize(msg, JsonCtx.Default.WsMessage);
        await _sessionManager.SendToAsync(sessionId, payload);

        try
        {
            var result = await responseTask;
            var kind = result?.ValueKind ?? System.Text.Json.JsonValueKind.Undefined;
            var resultStr = BrowserPluginRpcText.TryParseResultString(result);
            if (BrowserPluginRpcText.IsEffectivelyEmpty(resultStr))
            {
                _logger.LogDebug("[Browser] page_agent empty RPC result ValueKind={Kind}", kind);
                return BrowserPluginRpcText.PageAgentEmptyNotice(kind);
            }

            return resultStr!;
        }
        catch (Exception ex)
        {
            return "失败：page_agent 执行出错（" + ex.Message + "）。";
        }
    }

    /// <summary>在当前标签页执行 AI 提供的 JavaScript；与 <see cref="PageAgentAsync"/> 分离。</summary>
    [ToolFunction("run_custom_javascript_in_page")]
    [Description(
        "【自定义页内 JS】在用户当前浏览器标签页的页面上下文执行。参数 scriptCode 为整段 JavaScript，**必须包含 return**（如 return document.title；或 return document.querySelector('article')?.innerText ?? ''）。\n" +
        "适用：抽取正文/列表、滚动后取样、复杂 DOM 等「读页」任务；脚本宜短小、选择器明确；若返回空应改选择器或兜底，勿与 page_agent 的 observe 短标签混为一谈。\n" +
        "与 page_agent 分工：点击、填表、滚到控件、等待控件可见等操作用 page_agent；总结当前页长文正文用本工具而非反复 observe。\n" +
        "须扩展已开启 Allow User Scripts；执行前可能需用户确认（视安全设置）。")]
    public async Task<string> RunCustomJavaScriptInPageAsync(
        [Description("页内执行的 JS；须含 return。读正文示例：return document.querySelector('article')?.innerText ?? document.body.innerText.slice(0,8000);")]
        string scriptCode)
    {
        const int MaxScriptCodeLength = 32 * 1024;
        if (string.IsNullOrWhiteSpace(scriptCode))
            return "失败：scriptCode 不能为空。";
        if (scriptCode.Length > MaxScriptCodeLength)
            return $"失败：脚本长度超过限制（最大 {MaxScriptCodeLength} 字符）。";

        var sessionId = SessionContext.GetSessionId();
        _logger.LogInformation("[Browser] run_custom_javascript_in_page sessionId={SessionId} codeLen={Len}", sessionId ?? "(null)", scriptCode.Length);
        if (string.IsNullOrEmpty(sessionId))
            return "Error: Session ID is missing (no active chat context).";

        if (_sessionManager.Get(sessionId) == null)
            return "Error: WebSocket connection not found for this session.";

        var reqId = _rpcManager.RegisterRequest(out var responseTask);
        var msg = new WsMessage
        {
            Type = "rpc_request",
            Id = reqId,
            Method = "run_custom_javascript_in_page",
            Params = JsonSerializer.SerializeToElement(new { scriptCode })
        };
        var payload = JsonSerializer.Serialize(msg, JsonCtx.Default.WsMessage);
        await _sessionManager.SendToAsync(sessionId, payload);

        try
        {
            var result = await responseTask;
            var kind = result?.ValueKind ?? System.Text.Json.JsonValueKind.Undefined;
            var resultStr = BrowserPluginRpcText.TryParseResultString(result);
            if (BrowserPluginRpcText.IsEffectivelyEmpty(resultStr))
            {
                _logger.LogDebug("[Browser] run_custom_javascript_in_page empty RPC result ValueKind={Kind}", kind);
                return BrowserPluginRpcText.CustomScriptEmptyNotice(kind);
            }

            return resultStr!;
        }
        catch (Exception ex)
        {
            return "失败：执行自定义页面脚本时出错（" + ex.Message + "）。";
        }
    }

    [ToolFunction("capture_full_page")]
    [Description("Captures a full-page screenshot of the user's current browser tab. Returns a screenshot reference (e.g. screenshot:xxx) that must be passed to save_screenshot_to_downloads to save to the Downloads folder. Do not pass image data to the AI.")]
    public async Task<string> CaptureFullPageAsync()
    {
        var sessionId = SessionContext.GetSessionId();
        _logger.LogInformation("[Browser] capture_full_page sessionId={SessionId}", sessionId ?? "(null)");
        if (string.IsNullOrEmpty(sessionId))
            return "Error: Session ID is missing (no active chat context).";
        if (_sessionManager.Get(sessionId) == null)
            return "Error: WebSocket connection not found for this session.";

        var reqId = _rpcManager.RegisterRequest(out var responseTask);
        var msg = new WsMessage
        {
            Type = "rpc_request",
            Id = reqId,
            Method = "capture_full_page",
            Params = JsonSerializer.SerializeToElement(new object())
        };
        var payload = JsonSerializer.Serialize(msg, JsonCtx.Default.WsMessage);
        await _sessionManager.SendToAsync(sessionId, payload);

        try
        {
            var result = await responseTask;
            if (result == null)
                return "失败：未收到前端截图数据。";

            if (result.Value.ValueKind != JsonValueKind.Object)
                return "失败：前端返回格式无效。";

            if (!result.Value.TryGetProperty("images", out var imagesEl) || imagesEl.ValueKind != JsonValueKind.Array)
                return "失败：前端未返回 images 数组。";

            var list = new List<byte[]>();
            foreach (var item in imagesEl.EnumerateArray())
            {
                var b64 = item.GetString();
                if (string.IsNullOrEmpty(b64)) continue;
                try
                {
                    list.Add(Convert.FromBase64String(b64));
                }
                catch
                {
                    _logger.LogWarning("[Browser] capture_full_page skip invalid base64 chunk");
                }
            }

            if (list.Count == 0)
                return "失败：没有有效的截图片段。";

            byte[] pngBytes;
            if (list.Count == 1)
            {
                pngBytes = list[0];
            }
            else
            {
                var totalHeight = 0;
                var maxWidth = 0;
                foreach (var bytes in list)
                {
                    using var img = SixLabors.ImageSharp.Image.Load<Rgba32>(bytes);
                    totalHeight += img.Height;
                    if (img.Width > maxWidth) maxWidth = img.Width;
                }

                using var stitched = new SixLabors.ImageSharp.Image<Rgba32>(maxWidth, totalHeight);
                var y = 0;
                foreach (var bytes in list)
                {
                    using var img = SixLabors.ImageSharp.Image.Load<Rgba32>(bytes);
                    stitched.Mutate(m => m.DrawImage(img, new SixLabors.ImageSharp.Point(0, y), 1f));
                    y += img.Height;
                }

                using var ms = new MemoryStream();
                stitched.SaveAsPng(ms);
                pngBytes = ms.ToArray();
            }

            var screenshotRef = _screenshotCache.Store(pngBytes);
            _logger.LogInformation("[Browser] capture_full_page ref={Ref} size={Size}", screenshotRef, pngBytes.Length);
            return "成功：截图已就绪，引用为 " + screenshotRef + "。请调用 save_screenshot_to_downloads 并传入此引用。";
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Browser] capture_full_page failed");
            return "失败：整页截图处理出错（" + ex.Message + "）。";
        }
    }
}

using System.ComponentModel;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;
using OfficeCopilot.Server.Services;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace OfficeCopilot.Server.Plugins;

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

    [KernelFunction("highlight_webpage_text")]
    [Description("Highlights specific text on the user's current active webpage.")]
    public async Task<string> HighlightTextAsync(
        [Description("The exact text or keyword to highlight on the webpage")] string text,
        [Description("The CSS color to use for highlighting (e.g., 'yellow', '#ff0000'). Default is yellow.")] string color = "yellow",
        KernelArguments? arguments = null)
    {
        var sessionId = arguments?.TryGetValue("sessionId", out var sidObj) == true && sidObj is string s ? s : SessionContext.GetSessionId();
        _logger.LogInformation("[Browser] highlight_webpage_text text={Text} color={Color} sessionId={SessionId}", text, color, sessionId ?? "(null)");
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
            _logger.LogInformation("[Browser] highlight_webpage_text reqId={ReqId} result={Result}", reqId, resultMsg);
            return resultMsg;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Browser] highlight_webpage_text reqId={ReqId} failed", reqId);
            return "失败：浏览器高亮执行出错（" + ex.Message + "）。请确认当前标签页允许扩展注入脚本。";
        }
    }

    [KernelFunction("add_floating_note")]
    [Description("Adds a floating sticky note on the user's current webpage. Content and title are fully customizable. Multiple notes can exist at once. Optionally anchor the note above a specific text snippet.")]
    public async Task<string> AddFloatingNoteAsync(
        [Description("The main content of the floating note (AI can write any explanation, translation, or tip here)")] string message,
        [Description("Optional. Custom title shown in the note header (e.g. '翻译', '解释'). Default is 'Office Copilot'.")] string? title = null,
        [Description("Optional. If provided, the note will be positioned above the first occurrence of this text on the page; otherwise shown at top-right corner.")] string? anchorText = null,
        KernelArguments? arguments = null)
    {
        var sessionId = arguments?.TryGetValue("sessionId", out var sidObj) == true && sidObj is string s ? s : SessionContext.GetSessionId();
        _logger.LogInformation("[Browser] add_floating_note sessionId={SessionId} anchor={Anchor}", sessionId ?? "(null)", anchorText ?? "(default)");
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

    /// <summary>MCP 风格工具：在当前标签页执行预定义页面脚本，仅支持白名单内的 scriptId。</summary>
    [KernelFunction("run_page_script")]
    [Description("运行位置：用户当前浏览器标签页的页面上下文中（由扩展注入执行）。在当前标签页执行预定义脚本，仅支持白名单内的 scriptId。使用 scriptId 指定脚本（如 scroll_to_top、get_visible_text），paramsJson 为可选 JSON 参数。")]
    public async Task<string> RunPageScriptAsync(
        [Description("Predefined script ID (e.g. scroll_to_top, scroll_to_bottom, get_visible_text, get_page_title). Must be in the allowed whitelist.")] string scriptId,
        [Description("Optional JSON string for script parameters, e.g. {} or {\"selector\":\"#main\"}. Default empty object.")] string paramsJson = "{}",
        KernelArguments? arguments = null)
    {
        var sessionId = arguments?.TryGetValue("sessionId", out var sidObj) == true && sidObj is string s ? s : SessionContext.GetSessionId();
        _logger.LogInformation("[Browser] run_page_script scriptId={ScriptId} sessionId={SessionId}", scriptId, sessionId ?? "(null)");
        if (string.IsNullOrEmpty(sessionId))
            return "Error: Session ID is missing (no active chat context).";

        if (_sessionManager.Get(sessionId) == null)
            return "Error: WebSocket connection not found for this session.";

        var reqId = _rpcManager.RegisterRequest(out var responseTask);
        var msg = new WsMessage
        {
            Type = "rpc_request",
            Id = reqId,
            Method = "run_page_script",
            Params = JsonSerializer.SerializeToElement(new { scriptId, scriptParams = paramsJson })
        };
        var payload = JsonSerializer.Serialize(msg, JsonCtx.Default.WsMessage);
        await _sessionManager.SendToAsync(sessionId, payload);

        try
        {
            var result = await responseTask;
            var resultStr = (result?.ValueKind == System.Text.Json.JsonValueKind.String ? result.Value.GetString() : null) ?? result?.ToString();
            return string.IsNullOrEmpty(resultStr) ? "成功：页面脚本已执行。" : resultStr;
        }
        catch (Exception ex)
        {
            return "失败：执行页面脚本时出错（" + ex.Message + "）。";
        }
    }

    /// <summary>在当前标签页执行 AI 提供的 JavaScript 代码并返回结果；仅用于无预定义脚本时的兜底。需用户 HITL 确认后执行。</summary>
    [KernelFunction("run_custom_page_script")]
    [Description("运行位置：用户当前浏览器标签页的页面上下文中。执行 AI 提供的一段 JavaScript 代码字符串并返回执行结果（如 return document.title）。仅当没有合适预定义脚本时使用此兜底能力。执行前需用户确认。")]
    public async Task<string> RunCustomPageScriptAsync(
        [Description("要在页面上下文中执行的 JavaScript 代码，应包含 return 语句返回结果（如 return JSON.stringify([...document.images].map(i => i.src));）。")] string scriptCode,
        KernelArguments? arguments = null)
    {
        const int MaxScriptCodeLength = 32 * 1024;
        if (string.IsNullOrWhiteSpace(scriptCode))
            return "失败：scriptCode 不能为空。";
        if (scriptCode.Length > MaxScriptCodeLength)
            return $"失败：脚本长度超过限制（最大 {MaxScriptCodeLength} 字符）。";

        var sessionId = arguments?.TryGetValue("sessionId", out var sidObj) == true && sidObj is string s ? s : SessionContext.GetSessionId();
        _logger.LogInformation("[Browser] run_custom_page_script sessionId={SessionId} codeLen={Len}", sessionId ?? "(null)", scriptCode.Length);
        if (string.IsNullOrEmpty(sessionId))
            return "Error: Session ID is missing (no active chat context).";

        if (_sessionManager.Get(sessionId) == null)
            return "Error: WebSocket connection not found for this session.";

        var reqId = _rpcManager.RegisterRequest(out var responseTask);
        var msg = new WsMessage
        {
            Type = "rpc_request",
            Id = reqId,
            Method = "run_custom_page_script",
            Params = JsonSerializer.SerializeToElement(new { scriptCode })
        };
        var payload = JsonSerializer.Serialize(msg, JsonCtx.Default.WsMessage);
        await _sessionManager.SendToAsync(sessionId, payload);

        try
        {
            var result = await responseTask;
            var resultStr = (result?.ValueKind == System.Text.Json.JsonValueKind.String ? result.Value.GetString() : null) ?? result?.ToString();
            return string.IsNullOrEmpty(resultStr) ? "成功：自定义页面脚本已执行。" : resultStr;
        }
        catch (Exception ex)
        {
            return "失败：执行自定义页面脚本时出错（" + ex.Message + "）。";
        }
    }

    [KernelFunction("capture_full_page")]
    [Description("Captures a full-page screenshot of the user's current browser tab. Returns a screenshot reference (e.g. screenshot:xxx) that must be passed to save_screenshot_to_downloads to save to the Downloads folder. Do not pass image data to the AI.")]
    public async Task<string> CaptureFullPageAsync(KernelArguments? arguments = null)
    {
        var sessionId = arguments?.TryGetValue("sessionId", out var sidObj) == true && sidObj is string s ? s : SessionContext.GetSessionId();
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

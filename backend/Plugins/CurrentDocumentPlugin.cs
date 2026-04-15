using System.ComponentModel;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using OfficeCopilot.Server;
using OfficeCopilot.Server.Services;
using OfficeCopilot.Server.Services.ToolInvocation;

namespace OfficeCopilot.Server.Plugins;

/// <summary>RPC 调用当前打开的 Office 文档（Word/Excel 任务窗格）。仅当用户从 Word 或 Excel 加载项连接时有效。</summary>
[CopilotPluginId("CurrentDocument")]
public sealed class CurrentDocumentPlugin
{
    private readonly SessionManager _sessionManager;
    private readonly RpcManager _rpcManager;
    private readonly ILogger<CurrentDocumentPlugin> _logger;

    public CurrentDocumentPlugin(SessionManager sessionManager, RpcManager rpcManager, ILogger<CurrentDocumentPlugin> logger)
    {
        _sessionManager = sessionManager;
        _rpcManager = rpcManager;
        _logger = logger;
    }

    private async Task<string> SendRpcAsync(string sessionId, string method, object? paramsObj, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(sessionId))
            return "Error: Session ID is missing (no active chat context).";

        var ws = _sessionManager.Get(sessionId);
        if (ws == null)
            return "Error: WebSocket connection not found. Please open the task pane in Word, Excel, or PowerPoint and connect first.";

        var reqId = _rpcManager.RegisterRequest(out var responseTask);
        var payload = JsonSerializer.Serialize(new WsMessage
        {
            Type = "rpc_request",
            Id = reqId,
            Method = method,
            Params = paramsObj != null ? JsonSerializer.SerializeToElement(paramsObj) : null
        }, JsonCtx.Default.WsMessage);
        await _sessionManager.SendToAsync(sessionId, payload);

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(55));
            var result = await responseTask.WaitAsync(cts.Token);
            return result?.ToString() ?? "OK";
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[CurrentDocument] RPC method={Method} reqId={ReqId} failed", method, reqId);
            return "失败：" + ex.Message + "。若当前为浏览器侧边栏，请在 Word、Excel 或 PowerPoint 中打开本助手任务窗格以使用此功能。";
        }
    }

    [ToolFunction("current_word_insert_text")]
    [Description("在当前打开的 Word 文档末尾插入一段文字（每次调用独占一个新段落；多段/多级标题请多次调用，勿在同一次调用里混写多段再指望不同样式）。可选 style 指定段落样式（Heading1、Heading2、Normal、Title 等）。仅当用户从 Word 任务窗格连接时可用。")]
    public Task<string> CurrentWordInsertTextAsync(
        [Description("要插入的正文内容")] string text,
        [Description("可选段落样式：Heading1、Heading2、Heading3、Normal、Title、Subtitle")] string? style = null,
        CancellationToken cancellationToken = default)
    {
        var sessionId = SessionContext.GetSessionId();
        _logger.LogInformation("[CurrentDocument] current_word_insert_text sessionId={SessionId} style={Style}", sessionId ?? "(null)", style ?? "(none)");
        return SendRpcAsync(sessionId!, "word_insert_text", new { text, style }, cancellationToken);
    }

    [ToolFunction("current_word_read_body")]
    [Description("读取当前打开的 Word 文档正文（可选截断长度）。仅当用户从 Word 任务窗格连接时可用。")]
    public Task<string> CurrentWordReadBodyAsync(
        [Description("最大返回字符数，默认 8000")] int maxLength = 8000,
        CancellationToken cancellationToken = default)
    {
        var sessionId = SessionContext.GetSessionId();
        _logger.LogInformation("[CurrentDocument] current_word_read_body sessionId={SessionId} maxLength={MaxLength}", sessionId ?? "(null)", maxLength);
        return SendRpcAsync(sessionId!, "word_read_body", new { maxLength }, cancellationToken);
    }

    [ToolFunction("current_excel_read_range")]
    [Description("读取当前打开的 Excel 工作表中指定区域的数据。仅当用户从 Excel 任务窗格连接时可用。")]
    public async Task<string> CurrentExcelReadRangeAsync(
        [Description("区域地址，如 A1 或 A1:C5")] string address = "A1",
        [Description("工作表名称，不填则使用当前活动表")] string? sheetName = null,
        CancellationToken cancellationToken = default)
    {
        var sessionId = SessionContext.GetSessionId();
        _logger.LogInformation("[CurrentDocument] current_excel_read_range sessionId={SessionId} address={Address}", sessionId ?? "(null)", address);
        var result = await SendRpcAsync(sessionId!, "excel_read_range", new { address, sheetName }, cancellationToken).ConfigureAwait(false);
        var len = result?.Length ?? 0;
        _logger.LogDebug("[CurrentDocument] current_excel_read_range responseChars={Chars} preview={Preview}", len, TruncateForLog(result, 120));
        return result ?? "";
    }

    [ToolFunction("current_excel_write_range")]
    [Description("向当前打开的 Excel 工作表的指定区域写入数据（二维数组）。仅当用户从 Excel 任务窗格连接时可用。")]
    public Task<string> CurrentExcelWriteRangeAsync(
        [Description("区域左上角或完整区域，如 A1 或 A1:C3")] string address,
        [Description("二维数组 JSON，如 [[1,2],[3,4]]")] string data,
        [Description("工作表名称，不填则使用当前活动表")] string? sheetName = null,
        CancellationToken cancellationToken = default)
    {
        var sessionId = SessionContext.GetSessionId();
        object? values;
        try
        {
            values = JsonSerializer.Deserialize<object>(data);
        }
        catch
        {
            return Task.FromResult("失败：data 不是合法 JSON，请使用二维数组格式如 [[1,2],[3,4]]。");
        }

        var dataLength = data.Length;
        if (!ExcelWriteRangeDataShape.TryGetJaggedShape(values, out var rows, out var firstRowCols, out var maxCols, out var uniformRectangle))
        {
            _logger.LogInformation(
                "[CurrentDocument] current_excel_write_range sessionId={SessionId} address={Address} dataLength={DataLength} shape={Shape}",
                sessionId ?? "(null)", address, dataLength, "invalid_not_2d_json_array");
        }
        else
        {
            _logger.LogInformation(
                "[CurrentDocument] current_excel_write_range sessionId={SessionId} address={Address} dataLength={DataLength} rows={Rows} firstRowCols={FirstRowCols} maxCols={MaxCols} uniformRectangle={Uniform}",
                sessionId ?? "(null)", address, dataLength, rows, firstRowCols, maxCols, uniformRectangle);

            if ((rows > 1 || maxCols > 1) && ExcelWriteRangeDataShape.LooksLikeSingleCellAddress(address))
            {
                _logger.LogWarning(
                    "[CurrentDocument] current_excel_write_range singleCellAddressWithMultiCellData sessionId={SessionId} address={Address} rows={Rows} maxCols={MaxCols} hint=WPS_may_only_fill_top_left_use_explicit_range",
                    sessionId ?? "(null)", address, rows, maxCols);
            }
        }

        return SendRpcAsync(sessionId!, "excel_write_range", new { address, values, sheetName }, cancellationToken);
    }

    private static string TruncateForLog(string? text, int maxChars)
    {
        if (string.IsNullOrEmpty(text))
            return "";
        var t = text.Replace('\r', ' ').Replace('\n', ' ');
        if (t.Length <= maxChars)
            return t;
        return t[..maxChars] + "…";
    }

    [ToolFunction("current_word_read_selection")]
    [Description("读取当前打开的 Word 文档中用户选中的文本。仅当用户从 Word 任务窗格连接时可用。")]
    public Task<string> CurrentWordReadSelectionAsync(
        CancellationToken cancellationToken = default)
    {
        var sessionId = SessionContext.GetSessionId();
        _logger.LogInformation("[CurrentDocument] current_word_read_selection sessionId={SessionId}", sessionId ?? "(null)");
        return SendRpcAsync(sessionId!, "word_read_selection", null, cancellationToken);
    }

    [ToolFunction("current_word_insert_table")]
    [Description("在当前打开的 Word 文档中插入表格。仅当用户从 Word 任务窗格连接时可用。")]
    public Task<string> CurrentWordInsertTableAsync(
        [Description("行数")] int rowCount,
        [Description("列数")] int columnCount,
        [Description("可选：单元格内容二维数组 JSON，如 [[\"A\",\"B\"],[\"C\",\"D\"]]")] string? data = null,
        [Description("插入位置：End 文档末尾，Start 文档开头，或与选区相关")] string? insertLocation = "End",
        CancellationToken cancellationToken = default)
    {
        var sessionId = SessionContext.GetSessionId();
        object? values = null;
        if (!string.IsNullOrWhiteSpace(data))
        {
            try
            {
                values = JsonSerializer.Deserialize<object>(data);
            }
            catch
            {
                return Task.FromResult("失败：data 不是合法 JSON。");
            }
        }
        _logger.LogInformation("[CurrentDocument] current_word_insert_table sessionId={SessionId} rows={Rows} cols={Cols}", sessionId ?? "(null)", rowCount, columnCount);
        return SendRpcAsync(sessionId!, "word_insert_table", new { rowCount, columnCount, values, insertLocation }, cancellationToken);
    }

    [ToolFunction("current_excel_list_sheets")]
    [Description("列出当前打开的 Excel 工作簿中所有工作表名称。仅当用户从 Excel 任务窗格连接时可用。")]
    public Task<string> CurrentExcelListSheetsAsync(
        CancellationToken cancellationToken = default)
    {
        var sessionId = SessionContext.GetSessionId();
        _logger.LogInformation("[CurrentDocument] current_excel_list_sheets sessionId={SessionId}", sessionId ?? "(null)");
        return SendRpcAsync(sessionId!, "excel_list_sheets", null, cancellationToken);
    }

    [ToolFunction("current_excel_get_used_range")]
    [Description("获取当前工作表中已使用区域的地址与数据。仅当用户从 Excel 任务窗格连接时可用。")]
    public Task<string> CurrentExcelGetUsedRangeAsync(
        [Description("工作表名称，不填则使用当前活动表")] string? sheetName = null,
        CancellationToken cancellationToken = default)
    {
        var sessionId = SessionContext.GetSessionId();
        _logger.LogInformation("[CurrentDocument] current_excel_get_used_range sessionId={SessionId}", sessionId ?? "(null)");
        return SendRpcAsync(sessionId!, "excel_get_used_range", new { sheetName }, cancellationToken);
    }

    [ToolFunction("current_excel_read_formulas")]
    [Description("读取当前 Excel 工作表中指定区域的公式。仅当用户从 Excel 任务窗格连接时可用。")]
    public Task<string> CurrentExcelReadFormulasAsync(
        [Description("区域地址，如 A1 或 A1:C5")] string address = "A1",
        [Description("工作表名称，不填则使用当前活动表")] string? sheetName = null,
        CancellationToken cancellationToken = default)
    {
        var sessionId = SessionContext.GetSessionId();
        _logger.LogInformation("[CurrentDocument] current_excel_read_formulas sessionId={SessionId} address={Address}", sessionId ?? "(null)", address);
        return SendRpcAsync(sessionId!, "excel_read_formulas", new { address, sheetName }, cancellationToken);
    }

    [ToolFunction("current_excel_write_formulas")]
    [Description("向当前 Excel 工作表的指定区域写入公式（二维数组）。仅当用户从 Excel 任务窗格连接时可用。")]
    public Task<string> CurrentExcelWriteFormulasAsync(
        [Description("区域左上角或完整区域，如 A1")] string address,
        [Description("公式二维数组 JSON，如 [[\"=A1+B1\",\"=SUM(A1:A10)\"]]")] string formulasJson,
        [Description("工作表名称，不填则使用当前活动表")] string? sheetName = null,
        CancellationToken cancellationToken = default)
    {
        var sessionId = SessionContext.GetSessionId();
        object? formulas;
        try
        {
            formulas = JsonSerializer.Deserialize<object>(formulasJson);
        }
        catch
        {
            return Task.FromResult("失败：formulasJson 不是合法 JSON。");
        }
        _logger.LogInformation("[CurrentDocument] current_excel_write_formulas sessionId={SessionId} address={Address}", sessionId ?? "(null)", address);
        return SendRpcAsync(sessionId!, "excel_write_formulas", new { address, formulas, sheetName }, cancellationToken);
    }

    [ToolFunction("current_word_search_replace")]
    [Description("在当前 Word 文档正文或选区内查找并替换文本。仅当用户从 Word 任务窗格连接时可用。")]
    public Task<string> CurrentWordSearchReplaceAsync(
        [Description("要查找的文本")] string searchText,
        [Description("替换成的文本")] string replaceText,
        [Description("是否全部替换，默认 true。JSON 布尔或字符串均可。")] JsonElement? replaceAll = null,
        CancellationToken cancellationToken = default)
    {
        if (!ToolScalarArgumentParser.TryReadBoolWithDefault(replaceAll, true, out var replaceAllValue))
            return Task.FromResult("失败：replaceAll 无效，请使用 true/false 或字符串 \"true\"/\"false\"。");
        var sessionId = SessionContext.GetSessionId();
        _logger.LogInformation("[CurrentDocument] current_word_search_replace sessionId={SessionId}", sessionId ?? "(null)");
        return SendRpcAsync(sessionId!, "word_search_replace", new { searchText, replaceText, replaceAll = replaceAllValue }, cancellationToken);
    }

    [ToolFunction("current_ppt_document_create")]
    [Description(
        "新建演示文稿并保存到 filePath（须 .pptx 或 .pptm）。WPS 演示：任务窗格会 Presentations.Add 后 SaveAs。Microsoft PowerPoint 任务窗格：Office.js 无法自动保存到磁盘路径，仅会打开空白演示，需用户在 PowerPoint 中「另存为」到目标路径。仅 PowerPoint 或 WPS 演示 任务窗格连接时可用。")]
    public Task<string> CurrentPptDocumentCreateAsync(
        [Description("绝对路径，扩展名须为 .pptx 或 .pptm")] string filePath,
        CancellationToken cancellationToken = default)
    {
        var trimmed = (filePath ?? "").Trim();
        if (string.IsNullOrEmpty(trimmed))
            return Task.FromResult("失败：filePath 不能为空。");
        var lower = trimmed.ToLowerInvariant();
        if (!lower.EndsWith(".pptx", StringComparison.OrdinalIgnoreCase) && !lower.EndsWith(".pptm", StringComparison.OrdinalIgnoreCase))
            return Task.FromResult("失败：filePath 须为 .pptx 或 .pptm。");
        var sessionId = SessionContext.GetSessionId();
        _logger.LogInformation("[CurrentDocument] current_ppt_document_create sessionId={SessionId} filePath={FilePath}", sessionId ?? "(null)", trimmed);
        return SendRpcAsync(sessionId!, "ppt_document_create", new { filePath = trimmed }, cancellationToken);
    }

    [ToolFunction("current_ppt_slides_list")]
    [Description("列出当前打开的 PPT 演示文稿中所有幻灯片（按播放顺序）。仅当用户从 PowerPoint 或 WPS 演示 任务窗格连接时可用。回答用户时必须引用并归纳本工具输出中的要点，勿假设用户能看到工具原始返回。")]
    public Task<string> CurrentPptSlidesListAsync(
        CancellationToken cancellationToken = default)
    {
        var sessionId = SessionContext.GetSessionId();
        _logger.LogInformation("[CurrentDocument] current_ppt_slides_list sessionId={SessionId}", sessionId ?? "(null)");
        return SendRpcAsync(sessionId!, "ppt_slides_list", null, cancellationToken);
    }

    [ToolFunction("current_ppt_slide_read")]
    [Description("按播放顺序读取当前演示文稿中指定幻灯片的文本。slideIndex 从 1 开始；includeShapeDetails 为 true 时附加形状编号列表。仅当用户从 PowerPoint 或 WPS 演示 任务窗格连接时可用。回答用户时必须引用并归纳本工具输出中的正文与要点，勿假设用户能看到工具原始返回。")]
    public Task<string> CurrentPptSlideReadAsync(
        [Description("幻灯片序号，从 1 开始")] int slideIndex = 1,
        [Description("是否附加形状列表（默认 true）。JSON 布尔或字符串均可。")] JsonElement? includeShapeDetails = null,
        CancellationToken cancellationToken = default)
    {
        if (!ToolScalarArgumentParser.TryReadBoolWithDefault(includeShapeDetails, true, out var includeShapeDetailsValue))
            return Task.FromResult("失败：includeShapeDetails 无效，请使用 true/false 或字符串 \"true\"/\"false\"。");
        var sessionId = SessionContext.GetSessionId();
        _logger.LogInformation("[CurrentDocument] current_ppt_slide_read sessionId={SessionId} slideIndex={SlideIndex}", sessionId ?? "(null)", slideIndex);
        return SendRpcAsync(sessionId!, "ppt_slide_read", new { slideIndex, includeShapeDetails = includeShapeDetailsValue }, cancellationToken);
    }

    [ToolFunction("current_ppt_slide_write")]
    [Description("向当前演示文稿指定幻灯片写入文本：可选 shapeIndex/shapeName，或 placeholderType（title/body/subtitle/ctrTitle）。仅当用户从 PowerPoint 或 WPS 演示 任务窗格连接时可用。")]
    public Task<string> CurrentPptSlideWriteAsync(
        [Description("幻灯片序号，从 1 开始")] int slideIndex,
        [Description("占位符类型：title、body、subtitle、ctrTitle 等")] string placeholderType,
        [Description("要写入的文本")] string text,
        [Description("可选形状编号，见 ppt_slide_read")] int shapeIndex = 0,
        [Description("可选形状 Name")] string shapeName = "",
        CancellationToken cancellationToken = default)
    {
        var sessionId = SessionContext.GetSessionId();
        _logger.LogInformation("[CurrentDocument] current_ppt_slide_write sessionId={SessionId} slideIndex={SlideIndex}", sessionId ?? "(null)", slideIndex);
        return SendRpcAsync(sessionId!, "ppt_slide_write", new { slideIndex, placeholderType, text, shapeIndex, shapeName }, cancellationToken);
    }

    [ToolFunction("current_ppt_slide_insert")]
    [Description("在当前演示文稿中插入新幻灯片。仅当用户从 PowerPoint 或 WPS 演示 任务窗格连接时可用。")]
    public Task<string> CurrentPptSlideInsertAsync(
        [Description("插入位置：1 表示在第 1 页后插入，0 表示插入到最前；不传则插入到末尾")] int? position = null,
        [Description("新幻灯片标题文本")] string titleText = "",
        [Description("新幻灯片正文文本")] string bodyText = "",
        CancellationToken cancellationToken = default)
    {
        var sessionId = SessionContext.GetSessionId();
        _logger.LogInformation("[CurrentDocument] current_ppt_slide_insert sessionId={SessionId} position={Position}", sessionId ?? "(null)", position);
        return SendRpcAsync(sessionId!, "ppt_slide_insert", new { position, titleText, bodyText }, cancellationToken);
    }

    [ToolFunction("current_ppt_slide_delete")]
    [Description("删除当前演示文稿中指定序号的幻灯片。仅当用户从 PowerPoint 或 WPS 演示 任务窗格连接时可用。")]
    public Task<string> CurrentPptSlideDeleteAsync(
        [Description("要删除的幻灯片序号，从 1 开始")] int slideIndex,
        CancellationToken cancellationToken = default)
    {
        var sessionId = SessionContext.GetSessionId();
        _logger.LogInformation("[CurrentDocument] current_ppt_slide_delete sessionId={SessionId} slideIndex={SlideIndex}", sessionId ?? "(null)", slideIndex);
        return SendRpcAsync(sessionId!, "ppt_slide_delete", new { slideIndex }, cancellationToken);
    }

    [ToolFunction("current_ppt_slide_image_add")]
    [Description("在当前演示文稿指定页插入本地图片。后台会尝试读取 imagePath 并附带 imageBase64，供 PowerPoint 任务窗格 addPicture 使用（与宿主同机且路径可读时）。仅演示文稿任务窗格连接时可用。")]
    public Task<string> CurrentPptSlideImageAddAsync(
        [Description("幻灯片序号，从 1 开始")] int slideIndex = 1,
        [Description("本地图片路径")] string imagePath = "",
        CancellationToken cancellationToken = default)
    {
        var sessionId = SessionContext.GetSessionId();
        _logger.LogInformation("[CurrentDocument] current_ppt_slide_image_add sessionId={SessionId}", sessionId ?? "(null)");
        string? imageBase64 = null;
        try
        {
            if (!string.IsNullOrWhiteSpace(imagePath))
            {
                var p = OpenXmlHelpers.ResolvePath(imagePath);
                if (File.Exists(p))
                    imageBase64 = Convert.ToBase64String(File.ReadAllBytes(p));
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[CurrentDocument] Skip imageBase64 for path {Path}", imagePath);
        }

        return SendRpcAsync(sessionId!, "ppt_slide_image_add", new { slideIndex, imagePath, imageBase64 }, cancellationToken);
    }

    [ToolFunction("current_ppt_notes_read")]
    [Description("读取当前演示文稿指定幻灯片的演讲者备注。WPS 演示任务窗格可用；PowerPoint 任务窗格因 Office.js 无备注 API 会返回明确说明。仅演示文稿任务窗格连接时可用。")]
    public Task<string> CurrentPptNotesReadAsync(
        [Description("幻灯片序号，从 1 开始")] int slideIndex = 1,
        CancellationToken cancellationToken = default)
    {
        var sessionId = SessionContext.GetSessionId();
        return SendRpcAsync(sessionId!, "ppt_notes_read", new { slideIndex }, cancellationToken);
    }

    [ToolFunction("current_ppt_notes_write")]
    [Description("写入当前演示文稿指定幻灯片的演讲者备注。WPS 演示任务窗格可用；PowerPoint 任务窗格因 Office.js 无备注 API 会返回明确说明。仅演示文稿任务窗格连接时可用。")]
    public Task<string> CurrentPptNotesWriteAsync(
        [Description("幻灯片序号，从 1 开始")] int slideIndex,
        [Description("备注文本")] string text,
        CancellationToken cancellationToken = default)
    {
        var sessionId = SessionContext.GetSessionId();
        return SendRpcAsync(sessionId!, "ppt_notes_write", new { slideIndex, text }, cancellationToken);
    }

    [ToolFunction("current_ppt_slides_reorder")]
    [Description("重排当前演示文稿全部幻灯片顺序。newOrder 如 2,3,1（长度须等于总页数）。任务窗格已实现（WPS MoveTo / PowerPoint moveTo）。仅演示文稿任务窗格连接时可用。")]
    public Task<string> CurrentPptSlidesReorderAsync(
        [Description("逗号分隔的新顺序")] string newOrder,
        CancellationToken cancellationToken = default)
    {
        var sessionId = SessionContext.GetSessionId();
        return SendRpcAsync(sessionId!, "ppt_slides_reorder", new { newOrder }, cancellationToken);
    }

    [ToolFunction("current_ppt_table_create")]
    [Description("在当前演示文稿指定页添加表格（行 1–20、列 1–10）。任务窗格已实现。仅演示文稿任务窗格连接时可用。")]
    public Task<string> CurrentPptTableCreateAsync(
        [Description("幻灯片序号")] int slideIndex,
        [Description("行数")] int rows,
        [Description("列数")] int cols,
        CancellationToken cancellationToken = default)
    {
        var sessionId = SessionContext.GetSessionId();
        return SendRpcAsync(sessionId!, "ppt_table_create", new { slideIndex, rows, cols }, cancellationToken);
    }

    [ToolFunction("current_ppt_table_write_cells")]
    [Description("向当前演示文稿指定页首张表格写入单元格文本（rowsCsv：| 分行、英文逗号分列）。任务窗格已实现。")]
    public Task<string> CurrentPptTableWriteCellsAsync(
        [Description("幻灯片序号")] int slideIndex,
        [Description("如 A1,B1|A2,B2")] string rowsCsv,
        CancellationToken cancellationToken = default)
    {
        var sessionId = SessionContext.GetSessionId();
        return SendRpcAsync(sessionId!, "ppt_table_write_cells", new { slideIndex, rowsCsv }, cancellationToken);
    }

    [ToolFunction("current_ppt_hyperlink_add")]
    [Description("为指定页某形状文本添加超链接（绝对 URL）。任务窗格已实现（PowerPoint 需 PowerPointApi 1.10+）。")]
    public Task<string> CurrentPptHyperlinkAddAsync(
        [Description("幻灯片序号")] int slideIndex,
        [Description("绝对 URL")] string url,
        [Description("形状编号")] int shapeIndex = 1,
        [Description("可选形状名称")] string shapeName = "",
        CancellationToken cancellationToken = default)
    {
        var sessionId = SessionContext.GetSessionId();
        return SendRpcAsync(sessionId!, "ppt_hyperlink_add", new { slideIndex, url, shapeIndex, shapeName }, cancellationToken);
    }

    [ToolFunction("current_ppt_slide_duplicate")]
    [Description("复制指定幻灯片（插入在其后）。任务窗格已实现（WPS Duplicate；PowerPoint 用 exportAsBase64 + insertSlidesFromBase64）。")]
    public Task<string> CurrentPptSlideDuplicateAsync(
        [Description("要复制的幻灯片序号")] int slideIndex,
        CancellationToken cancellationToken = default)
    {
        var sessionId = SessionContext.GetSessionId();
        return SendRpcAsync(sessionId!, "ppt_slide_duplicate", new { slideIndex }, cancellationToken);
    }

    [ToolFunction("current_run_document_script")]
    [Description("当前 Office/WPS 宿主文档内脚本（任务窗格注入）。检索词：宿主、Word、Excel、任务窗格、文档自动化。仅白名单 scriptId；仅 Office/WPS 侧栏连接时可用。")]
    public Task<string> CurrentRunDocumentScriptAsync(
        [Description("预定义脚本 ID，必须在前端 DOCUMENT_SCRIPTS 注册表中存在")] string scriptId,
        [Description("可选参数，JSON 对象字符串，如 {} 或 {\"key\":\"value\"}")] string? paramsJson = null,
        CancellationToken cancellationToken = default)
    {
        var sessionId = SessionContext.GetSessionId();
        object? scriptParams = null;
        if (!string.IsNullOrWhiteSpace(paramsJson))
        {
            try
            {
                scriptParams = JsonSerializer.Deserialize<object>(paramsJson);
            }
            catch
            {
                return Task.FromResult("失败：paramsJson 不是合法 JSON。");
            }
        }
        _logger.LogInformation("[CurrentDocument] current_run_document_script sessionId={SessionId} scriptId={ScriptId}", sessionId ?? "(null)", scriptId);
        return SendRpcAsync(sessionId!, "run_document_script", new { scriptId, scriptParams }, cancellationToken);
    }

    /// <summary>在当前 Office/WPS 文档环境中执行 AI 提供的脚本代码并返回结果；用作兜底。需用户 HITL 确认后执行。</summary>
    [ToolFunction("current_run_custom_document_script")]
    [Description("运行位置：当前打开的 Office/WPS 文档环境中。执行 AI 提供的一段脚本代码并返回结果。仅当没有合适预定义脚本时使用此兜底能力。仅 Office/WPS 任务窗格连接时可用。执行前需用户确认。")]
    public Task<string> CurrentRunCustomDocumentScriptAsync(
        [Description("要在文档上下文中执行的 JavaScript 代码，应包含 return 语句返回结果。")] string scriptCode,
        CancellationToken cancellationToken = default)
    {
        const int MaxScriptCodeLength = 32 * 1024;
        if (string.IsNullOrWhiteSpace(scriptCode))
            return Task.FromResult("失败：scriptCode 不能为空。");
        if (scriptCode.Length > MaxScriptCodeLength)
            return Task.FromResult($"失败：脚本长度超过限制（最大 {MaxScriptCodeLength} 字符）。");

        var sessionId = SessionContext.GetSessionId();
        _logger.LogInformation("[CurrentDocument] current_run_custom_document_script sessionId={SessionId} codeLen={Len}", sessionId ?? "(null)", scriptCode.Length);
        _logger.LogDebug(
            "[CurrentDocument] current_run_custom_document_script codePreview={Preview}",
            TruncateForLog(scriptCode.Trim(), 80));
        return SendRpcAsync(sessionId!, "run_custom_document_script", new { scriptCode = scriptCode.Trim() }, cancellationToken);
    }
}

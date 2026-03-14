using System.ComponentModel;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;
using OfficeCopilot.Server.Services;

namespace OfficeCopilot.Server.Plugins;

/// <summary>RPC 调用当前打开的 Office 文档（Word/Excel 任务窗格）。仅当用户从 Word 或 Excel 加载项连接时有效。</summary>
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
            return "Error: WebSocket connection not found. Please open the task pane in Word or Excel and connect first.";

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
            return "失败：" + ex.Message + "。若当前为浏览器侧边栏，请在 Word 或 Excel 中打开本助手任务窗格以使用此功能。";
        }
    }

    [KernelFunction("current_word_insert_text")]
    [Description("在当前打开的 Word 文档末尾插入一段文字。仅当用户从 Word 任务窗格连接时可用。")]
    public Task<string> CurrentWordInsertTextAsync(
        [Description("要插入的正文内容")] string text,
        KernelArguments? arguments = null,
        CancellationToken cancellationToken = default)
    {
        var sessionId = arguments?.TryGetValue("sessionId", out var sidObj) == true && sidObj is string s ? s : SessionContext.GetSessionId();
        _logger.LogInformation("[CurrentDocument] current_word_insert_text sessionId={SessionId}", sessionId ?? "(null)");
        return SendRpcAsync(sessionId!, "word_insert_text", new { text }, cancellationToken);
    }

    [KernelFunction("current_word_read_body")]
    [Description("读取当前打开的 Word 文档正文（可选截断长度）。仅当用户从 Word 任务窗格连接时可用。")]
    public Task<string> CurrentWordReadBodyAsync(
        [Description("最大返回字符数，默认 8000")] int maxLength = 8000,
        KernelArguments? arguments = null,
        CancellationToken cancellationToken = default)
    {
        var sessionId = arguments?.TryGetValue("sessionId", out var sidObj) == true && sidObj is string s ? s : SessionContext.GetSessionId();
        _logger.LogInformation("[CurrentDocument] current_word_read_body sessionId={SessionId} maxLength={MaxLength}", sessionId ?? "(null)", maxLength);
        return SendRpcAsync(sessionId!, "word_read_body", new { maxLength }, cancellationToken);
    }

    [KernelFunction("current_excel_read_range")]
    [Description("读取当前打开的 Excel 工作表中指定区域的数据。仅当用户从 Excel 任务窗格连接时可用。")]
    public Task<string> CurrentExcelReadRangeAsync(
        [Description("区域地址，如 A1 或 A1:C5")] string address = "A1",
        [Description("工作表名称，不填则使用当前活动表")] string? sheetName = null,
        KernelArguments? arguments = null,
        CancellationToken cancellationToken = default)
    {
        var sessionId = arguments?.TryGetValue("sessionId", out var sidObj) == true && sidObj is string s ? s : SessionContext.GetSessionId();
        _logger.LogInformation("[CurrentDocument] current_excel_read_range sessionId={SessionId} address={Address}", sessionId ?? "(null)", address);
        return SendRpcAsync(sessionId!, "excel_read_range", new { address, sheetName }, cancellationToken);
    }

    [KernelFunction("current_excel_write_range")]
    [Description("向当前打开的 Excel 工作表的指定区域写入数据（二维数组）。仅当用户从 Excel 任务窗格连接时可用。")]
    public Task<string> CurrentExcelWriteRangeAsync(
        [Description("区域左上角或完整区域，如 A1 或 A1:C3")] string address,
        [Description("二维数组 JSON，如 [[1,2],[3,4]]")] string valuesJson,
        [Description("工作表名称，不填则使用当前活动表")] string? sheetName = null,
        KernelArguments? arguments = null,
        CancellationToken cancellationToken = default)
    {
        var sessionId = arguments?.TryGetValue("sessionId", out var sidObj) == true && sidObj is string s ? s : SessionContext.GetSessionId();
        object? values;
        try
        {
            values = JsonSerializer.Deserialize<object>(valuesJson);
        }
        catch
        {
            return Task.FromResult("失败：valuesJson 不是合法 JSON，请使用二维数组格式如 [[1,2],[3,4]]。");
        }
        _logger.LogInformation("[CurrentDocument] current_excel_write_range sessionId={SessionId} address={Address}", sessionId ?? "(null)", address);
        return SendRpcAsync(sessionId!, "excel_write_range", new { address, values, sheetName }, cancellationToken);
    }

    [KernelFunction("current_word_read_selection")]
    [Description("读取当前打开的 Word 文档中用户选中的文本。仅当用户从 Word 任务窗格连接时可用。")]
    public Task<string> CurrentWordReadSelectionAsync(
        KernelArguments? arguments = null,
        CancellationToken cancellationToken = default)
    {
        var sessionId = arguments?.TryGetValue("sessionId", out var sidObj) == true && sidObj is string s ? s : SessionContext.GetSessionId();
        _logger.LogInformation("[CurrentDocument] current_word_read_selection sessionId={SessionId}", sessionId ?? "(null)");
        return SendRpcAsync(sessionId!, "word_read_selection", null, cancellationToken);
    }

    [KernelFunction("current_word_insert_table")]
    [Description("在当前打开的 Word 文档中插入表格。仅当用户从 Word 任务窗格连接时可用。")]
    public Task<string> CurrentWordInsertTableAsync(
        [Description("行数")] int rowCount,
        [Description("列数")] int columnCount,
        [Description("可选：单元格内容二维数组 JSON，如 [[\"A\",\"B\"],[\"C\",\"D\"]]")] string? valuesJson = null,
        [Description("插入位置：End 文档末尾，Start 文档开头，或与选区相关")] string? insertLocation = "End",
        KernelArguments? arguments = null,
        CancellationToken cancellationToken = default)
    {
        var sessionId = arguments?.TryGetValue("sessionId", out var sidObj) == true && sidObj is string s ? s : SessionContext.GetSessionId();
        object? values = null;
        if (!string.IsNullOrWhiteSpace(valuesJson))
        {
            try
            {
                values = JsonSerializer.Deserialize<object>(valuesJson);
            }
            catch
            {
                return Task.FromResult("失败：valuesJson 不是合法 JSON。");
            }
        }
        _logger.LogInformation("[CurrentDocument] current_word_insert_table sessionId={SessionId} rows={Rows} cols={Cols}", sessionId ?? "(null)", rowCount, columnCount);
        return SendRpcAsync(sessionId!, "word_insert_table", new { rowCount, columnCount, values, insertLocation }, cancellationToken);
    }

    [KernelFunction("current_excel_list_sheets")]
    [Description("列出当前打开的 Excel 工作簿中所有工作表名称。仅当用户从 Excel 任务窗格连接时可用。")]
    public Task<string> CurrentExcelListSheetsAsync(
        KernelArguments? arguments = null,
        CancellationToken cancellationToken = default)
    {
        var sessionId = arguments?.TryGetValue("sessionId", out var sidObj) == true && sidObj is string s ? s : SessionContext.GetSessionId();
        _logger.LogInformation("[CurrentDocument] current_excel_list_sheets sessionId={SessionId}", sessionId ?? "(null)");
        return SendRpcAsync(sessionId!, "excel_list_sheets", null, cancellationToken);
    }

    [KernelFunction("current_excel_get_used_range")]
    [Description("获取当前工作表中已使用区域的地址与数据。仅当用户从 Excel 任务窗格连接时可用。")]
    public Task<string> CurrentExcelGetUsedRangeAsync(
        [Description("工作表名称，不填则使用当前活动表")] string? sheetName = null,
        KernelArguments? arguments = null,
        CancellationToken cancellationToken = default)
    {
        var sessionId = arguments?.TryGetValue("sessionId", out var sidObj) == true && sidObj is string s ? s : SessionContext.GetSessionId();
        _logger.LogInformation("[CurrentDocument] current_excel_get_used_range sessionId={SessionId}", sessionId ?? "(null)");
        return SendRpcAsync(sessionId!, "excel_get_used_range", new { sheetName }, cancellationToken);
    }

    [KernelFunction("current_excel_read_formulas")]
    [Description("读取当前 Excel 工作表中指定区域的公式。仅当用户从 Excel 任务窗格连接时可用。")]
    public Task<string> CurrentExcelReadFormulasAsync(
        [Description("区域地址，如 A1 或 A1:C5")] string address = "A1",
        [Description("工作表名称，不填则使用当前活动表")] string? sheetName = null,
        KernelArguments? arguments = null,
        CancellationToken cancellationToken = default)
    {
        var sessionId = arguments?.TryGetValue("sessionId", out var sidObj) == true && sidObj is string s ? s : SessionContext.GetSessionId();
        _logger.LogInformation("[CurrentDocument] current_excel_read_formulas sessionId={SessionId} address={Address}", sessionId ?? "(null)", address);
        return SendRpcAsync(sessionId!, "excel_read_formulas", new { address, sheetName }, cancellationToken);
    }

    [KernelFunction("current_excel_write_formulas")]
    [Description("向当前 Excel 工作表的指定区域写入公式（二维数组）。仅当用户从 Excel 任务窗格连接时可用。")]
    public Task<string> CurrentExcelWriteFormulasAsync(
        [Description("区域左上角或完整区域，如 A1")] string address,
        [Description("公式二维数组 JSON，如 [[\"=A1+B1\",\"=SUM(A1:A10)\"]]")] string formulasJson,
        [Description("工作表名称，不填则使用当前活动表")] string? sheetName = null,
        KernelArguments? arguments = null,
        CancellationToken cancellationToken = default)
    {
        var sessionId = arguments?.TryGetValue("sessionId", out var sidObj) == true && sidObj is string s ? s : SessionContext.GetSessionId();
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

    [KernelFunction("current_word_search_replace")]
    [Description("在当前 Word 文档正文或选区内查找并替换文本。仅当用户从 Word 任务窗格连接时可用。")]
    public Task<string> CurrentWordSearchReplaceAsync(
        [Description("要查找的文本")] string searchText,
        [Description("替换成的文本")] string replaceText,
        [Description("是否全部替换，默认 true")] bool replaceAll = true,
        KernelArguments? arguments = null,
        CancellationToken cancellationToken = default)
    {
        var sessionId = arguments?.TryGetValue("sessionId", out var sidObj) == true && sidObj is string s ? s : SessionContext.GetSessionId();
        _logger.LogInformation("[CurrentDocument] current_word_search_replace sessionId={SessionId}", sessionId ?? "(null)");
        return SendRpcAsync(sessionId!, "word_search_replace", new { searchText, replaceText, replaceAll }, cancellationToken);
    }

    [KernelFunction("current_run_document_script")]
    [Description("在当前文档环境中执行预定义脚本（仅 scriptId 白名单）。用于长尾或组合操作，仅 Office/WPS 任务窗格连接时可用。")]
    public Task<string> CurrentRunDocumentScriptAsync(
        [Description("预定义脚本 ID，必须在前端 DOCUMENT_SCRIPTS 注册表中存在")] string scriptId,
        [Description("可选参数，JSON 对象字符串，如 {} 或 {\"key\":\"value\"}")] string? paramsJson = null,
        KernelArguments? arguments = null,
        CancellationToken cancellationToken = default)
    {
        var sessionId = arguments?.TryGetValue("sessionId", out var sidObj) == true && sidObj is string s ? s : SessionContext.GetSessionId();
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
}

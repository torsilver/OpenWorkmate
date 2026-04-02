using System.Text;
using Microsoft.SemanticKernel.ChatCompletion;

namespace OfficeCopilot.Server.Services;

/// <summary>两阶段子类召回的保守保底（Chrome + 历史中出现 Excel 语境时补 <c>Excel-修改样式</c>）。</summary>
internal static class ToolSelectionRecallHelper
{
    internal const string ExcelStyleSubcategoryId = "Excel-修改样式";

    internal static void MergeChromeExcelStyleSubcategoryIfNeeded(
        List<string> selectedSubcategoryIds,
        string? clientType,
        string? userMessage,
        ChatHistory? recentHistory,
        HashSet<string> validSubcategoryIds)
    {
        if (!IsChromeClient(clientType))
            return;
        if (!HistoryOrMessageSuggestsExcelContext(userMessage, recentHistory))
            return;
        if (!validSubcategoryIds.Contains(ExcelStyleSubcategoryId))
            return;
        if (selectedSubcategoryIds.Exists(s => string.Equals(s, ExcelStyleSubcategoryId, StringComparison.OrdinalIgnoreCase)))
            return;
        selectedSubcategoryIds.Add(ExcelStyleSubcategoryId);
    }

    internal static bool IsChromeClient(string? clientType)
    {
        var ct = (clientType ?? "").Trim();
        return string.IsNullOrEmpty(ct) || string.Equals(ct, "chrome", StringComparison.OrdinalIgnoreCase);
    }

    internal static bool HistoryOrMessageSuggestsExcelContext(string? userMessage, ChatHistory? recentHistory)
    {
        var sb = new StringBuilder();
        sb.Append(userMessage);
        sb.Append('\n');
        if (recentHistory != null)
        {
            var start = Math.Max(0, recentHistory.Count - 8);
            for (var i = start; i < recentHistory.Count; i++)
            {
                sb.Append(recentHistory[i].Content);
                sb.Append('\n');
            }
        }

        var blob = sb.ToString();
        if (string.IsNullOrWhiteSpace(blob))
            return false;

        return blob.Contains("xlsx", StringComparison.OrdinalIgnoreCase)
               || blob.Contains(".xls", StringComparison.OrdinalIgnoreCase)
               || blob.Contains("xlsm", StringComparison.OrdinalIgnoreCase)
               || blob.Contains("excel", StringComparison.OrdinalIgnoreCase)
               || blob.Contains("合并", StringComparison.OrdinalIgnoreCase)
               || blob.Contains("取消合并", StringComparison.OrdinalIgnoreCase)
               || blob.Contains("单元格", StringComparison.OrdinalIgnoreCase)
               || blob.Contains("工作表", StringComparison.OrdinalIgnoreCase);
    }
}

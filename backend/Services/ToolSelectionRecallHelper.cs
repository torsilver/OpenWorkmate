using System.Text;
using Microsoft.Extensions.AI;

namespace OfficeCopilot.Server.Services;

/// <summary>两阶段子类召回的保守保底（Chrome + 历史中出现 Excel 语境时补 <c>Excel-修改样式</c>）；Chrome 下一阶段不展示 CurrentDocument-*（该端 <see cref="ClientTypeToolFilter"/> 不暴露 CurrentDocument 插件）。</summary>
internal static class ToolSelectionRecallHelper
{
    internal const string ExcelStyleSubcategoryId = "Excel-修改样式";

    /// <summary>Chrome 侧无法调用 CurrentDocument 插件；从一阶段子类列表中移除 CurrentDocument-*，避免模型选中后候选函数被 <see cref="ClientTypeToolFilter.Filter"/> 清空。</summary>
    internal static List<(string Id, string Description)> ExcludeCurrentDocumentSubcategoriesForChrome(
        List<(string Id, string Description)> subcategories,
        string? clientType)
    {
        if (subcategories == null || subcategories.Count == 0)
            return subcategories ?? new List<(string, string)>();
        if (!IsChromeClient(clientType))
            return subcategories;
        return subcategories
            .Where(s => !s.Id.StartsWith("CurrentDocument-", StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    internal static void MergeChromeExcelStyleSubcategoryIfNeeded(
        List<string> selectedSubcategoryIds,
        string? clientType,
        string? userMessage,
        IReadOnlyList<ChatMessage>? recentHistory,
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

    internal static bool HistoryOrMessageSuggestsExcelContext(string? userMessage, IReadOnlyList<ChatMessage>? recentHistory)
    {
        var sb = new StringBuilder();
        sb.Append(userMessage);
        sb.Append('\n');
        if (recentHistory != null)
        {
            var start = Math.Max(0, recentHistory.Count - 8);
            for (var i = start; i < recentHistory.Count; i++)
            {
                sb.Append(recentHistory[i].Text);
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

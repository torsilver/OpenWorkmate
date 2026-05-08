using System.Text.Json;
using System.Text.RegularExpressions;

namespace OpenWorkmate.Server.Plugins;

/// <summary>解析 current_excel_write_range 的 data 是否为 JSON 二维数组并统计行列（供日志与单测）。</summary>
internal static class ExcelWriteRangeDataShape
{
    /// <summary>
    /// 将 <paramref name="values"/> 序列化为 <see cref="JsonElement"/> 后解析为锯齿二维数组形状。
    /// </summary>
    /// <returns>根为 JSON 数组且每个元素均为 JSON 数组时为 true（含空数组 outer）。</returns>
    public static bool TryGetJaggedShape(object? values, out int rows, out int firstRowCols, out int maxCols, out bool uniformRectangle)
    {
        rows = 0;
        firstRowCols = 0;
        maxCols = 0;
        uniformRectangle = true;

        if (values is null)
            return false;

        JsonElement root;
        try
        {
            root = JsonSerializer.SerializeToElement(values);
        }
        catch
        {
            return false;
        }

        return TryGetJaggedShape(root, out rows, out firstRowCols, out maxCols, out uniformRectangle);
    }

    public static bool TryGetJaggedShape(JsonElement root, out int rows, out int firstRowCols, out int maxCols, out bool uniformRectangle)
    {
        rows = 0;
        firstRowCols = 0;
        maxCols = 0;
        uniformRectangle = true;

        if (root.ValueKind != JsonValueKind.Array)
            return false;

        rows = root.GetArrayLength();
        if (rows == 0)
            return true;

        var first = root[0];
        if (first.ValueKind != JsonValueKind.Array)
            return false;

        firstRowCols = first.GetArrayLength();
        maxCols = firstRowCols;

        for (var i = 1; i < rows; i++)
        {
            var row = root[i];
            if (row.ValueKind != JsonValueKind.Array)
                return false;

            var len = row.GetArrayLength();
            if (len > maxCols)
                maxCols = len;
            if (len != firstRowCols)
                uniformRectangle = false;
        }

        return true;
    }

    /// <summary>是否为不含冒号的单格引用（如 A1），用于与多行多列 data 对照打诊断日志。</summary>
    public static bool LooksLikeSingleCellAddress(string? address)
    {
        var a = (address ?? "").Trim();
        if (a.Length == 0 || a.Contains(':', StringComparison.Ordinal))
            return false;
        return Regex.IsMatch(a, "^[A-Za-z]+\\d+$", RegexOptions.CultureInvariant);
    }
}

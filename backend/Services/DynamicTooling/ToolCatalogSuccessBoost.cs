using System.Collections.Concurrent;

namespace OpenWorkmate.Server.Services.DynamicTooling;

/// <summary>
/// 进程内累计各工具函数「语义成功」调用次数，供 <see cref="ToolCatalogIndex.Search"/> 在关键词打分之外做轻量 boost（遥测闭环前置，无持久化）。
/// </summary>
public static class ToolCatalogSuccessBoost
{
    private static readonly ConcurrentDictionary<string, int> SuccessCounts = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>在工具中间件语义成功路径调用，单函数计数上限避免无限增长。</summary>
    public static void RecordSuccess(string functionName)
    {
        if (string.IsNullOrEmpty(functionName)) return;
        SuccessCounts.AddOrUpdate(functionName, 1, (_, v) => Math.Min(v + 1, 10_000));
    }

    /// <summary>供 catalog 检索复制一份快照；可为空表示无数据。</summary>
    public static IReadOnlyDictionary<string, int> GetSnapshot()
    {
        if (SuccessCounts.IsEmpty)
            return new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        return new Dictionary<string, int>(SuccessCounts, StringComparer.OrdinalIgnoreCase);
    }
}

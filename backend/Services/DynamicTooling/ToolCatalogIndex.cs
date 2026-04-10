using Microsoft.Extensions.AI;

namespace OfficeCopilot.Server.Services.DynamicTooling;

/// <summary>允许列表上的工具轻量索引，供 <c>search_available_tools</c> 使用（不含完整 JSON Schema）。</summary>
public sealed class ToolCatalogIndex
{
    private readonly List<Entry> _entries;

    private ToolCatalogIndex(List<Entry> entries) => _entries = entries;

    public IReadOnlyList<Entry> Entries => _entries;

    public static ToolCatalogIndex BuildFromAllowedTools(ToolRegistry registry, string? clientType, string? sessionId)
    {
        var list = new List<Entry>();
        foreach (var (plugin, function, tool) in registry.GetAllWithMetadata())
        {
            if (!ClientTypeToolFilter.IsAllowed(plugin, function, clientType, sessionId))
                continue;
            if (DynamicToolingConstants.MetaFunctionNames.Contains(function))
                continue;
            var desc = tool.Description ?? "";
            list.Add(new Entry(plugin, function, desc));
        }
        list.Sort((a, b) => string.Compare(a.FunctionName, b.FunctionName, StringComparison.OrdinalIgnoreCase));
        return new ToolCatalogIndex(list);
    }

    /// <summary>简单关键词打分：query 拆词后在 plugin/function/desc 中的命中次数。空 query 时先放入 <paramref name="pinnedFunctionNames"/> 对应条目，再按函数名补满。</summary>
    public IReadOnlyList<Entry> Search(string? query, int topK, IReadOnlyCollection<string>? pinnedFunctionNames = null)
    {
        if (topK <= 0) return Array.Empty<Entry>();
        var q = (query ?? "").Trim();
        if (q.Length == 0)
            return SearchEmptyWithPinned(topK, pinnedFunctionNames);

        var terms = q.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (terms.Length == 0)
            return SearchEmptyWithPinned(topK, pinnedFunctionNames);

        var scored = new List<(Entry E, int Score)>(_entries.Count);
        foreach (var e in _entries)
        {
            var hay = $"{e.PluginName} {e.FunctionName} {e.ShortDescription}".AsSpan();
            var score = 0;
            foreach (var t in terms)
            {
                if (t.Length == 0) continue;
                if (hay.Contains(t, StringComparison.OrdinalIgnoreCase))
                    score += 2;
                if (e.FunctionName.Contains(t, StringComparison.OrdinalIgnoreCase))
                    score += 3;
            }
            scored.Add((e, score));
        }

        scored.Sort((a, b) =>
        {
            var c = b.Score.CompareTo(a.Score);
            return c != 0 ? c : string.Compare(a.E.FunctionName, b.E.FunctionName, StringComparison.OrdinalIgnoreCase);
        });

        var result = new List<Entry>(topK);
        foreach (var (e, s) in scored)
        {
            if (s <= 0 && terms.Length > 0) continue;
            result.Add(e);
            if (result.Count >= topK) break;
        }

        if (result.Count == 0)
        {
            foreach (var e in _entries)
            {
                result.Add(e);
                if (result.Count >= topK) break;
            }
        }

        return result;
    }

    /// <summary>空 query：先按字母序加入 pinned 中在目录里存在的项，再按全目录字母序填满 topK。</summary>
    private IReadOnlyList<Entry> SearchEmptyWithPinned(int topK, IReadOnlyCollection<string>? pinnedFunctionNames)
    {
        var result = new List<Entry>(topK);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (pinnedFunctionNames is { Count: > 0 })
        {
            foreach (var pin in pinnedFunctionNames.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
            {
                if (result.Count >= topK) break;
                var e = _entries.Find(x => string.Equals(x.FunctionName, pin, StringComparison.OrdinalIgnoreCase));
                if (e != null && seen.Add(e.FunctionName))
                    result.Add(e);
            }
        }

        foreach (var e in _entries)
        {
            if (result.Count >= topK) break;
            if (seen.Add(e.FunctionName))
                result.Add(e);
        }

        return result;
    }

    public sealed record Entry(string PluginName, string FunctionName, string ShortDescription);
}

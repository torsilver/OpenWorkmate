using OfficeCopilot.Server.Services;

namespace OfficeCopilot.Server.Services.DynamicTooling;

/// <summary>已启用用户技能的轻量索引，供 <c>search_available_skills</c> 使用（不含 SKILL 正文）。</summary>
public sealed class SkillCatalogIndex
{
    private const int MaxDescriptionIndexChars = 500;

    private readonly List<Entry> _entries;

    private SkillCatalogIndex(List<Entry> entries) => _entries = entries;

    public IReadOnlyList<Entry> Entries => _entries;

    public static SkillCatalogIndex Empty { get; } = new(new List<Entry>());

    /// <summary>仅收录 <see cref="SkillDefinition.Enabled"/> 为 true 的技能。</summary>
    public static SkillCatalogIndex BuildFromEnabledSkills(IReadOnlyList<SkillDefinition> skills)
    {
        var list = new List<Entry>();
        if (skills == null || skills.Count == 0)
            return new SkillCatalogIndex(list);

        foreach (var s in skills)
        {
            if (!s.Enabled) continue;
            var id = (s.Id ?? "").Trim();
            if (id.Length == 0) continue;
            var name = (s.Name ?? "").Trim();
            var desc = (s.Description ?? "").Trim();
            if (desc.Length > MaxDescriptionIndexChars)
                desc = desc[..MaxDescriptionIndexChars] + "…";
            var sanitized = UserSkillToolNaming.SanitizeToFunctionName(id);
            list.Add(new Entry(id, name, desc, sanitized));
        }

        list.Sort((a, b) => string.Compare(a.SkillId, b.SkillId, StringComparison.OrdinalIgnoreCase));
        return new SkillCatalogIndex(list);
    }

    /// <summary>关键词打分：拆词后在 Id、展示名、消毒名、描述中命中；空 query 时按 Id 字母序填满 topK。</summary>
    public IReadOnlyList<Entry> Search(string? query, int topK, IReadOnlyCollection<string>? pinnedSkillIds = null)
    {
        if (topK <= 0) return Array.Empty<Entry>();
        var q = (query ?? "").Trim();
        if (q.Length == 0)
            return SearchEmptyWithPinned(topK, pinnedSkillIds);

        var terms = q.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (terms.Length == 0)
            return SearchEmptyWithPinned(topK, pinnedSkillIds);

        var scored = new List<(Entry E, int Score)>(_entries.Count);
        foreach (var e in _entries)
        {
            var hay = $"{e.SkillId} {e.DisplayName} {e.SanitizedId} {e.ShortDescription}".AsSpan();
            var score = 0;
            foreach (var t in terms)
            {
                if (t.Length == 0) continue;
                if (hay.Contains(t, StringComparison.OrdinalIgnoreCase))
                    score += 2;
                if (e.SkillId.Contains(t, StringComparison.OrdinalIgnoreCase))
                    score += 3;
                if (e.SanitizedId.Contains(t, StringComparison.OrdinalIgnoreCase))
                    score += 2;
            }

            scored.Add((e, score));
        }

        scored.Sort((a, b) =>
        {
            var c = b.Score.CompareTo(a.Score);
            return c != 0 ? c : string.Compare(a.E.SkillId, b.E.SkillId, StringComparison.OrdinalIgnoreCase);
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

    private IReadOnlyList<Entry> SearchEmptyWithPinned(int topK, IReadOnlyCollection<string>? pinnedSkillIds)
    {
        var result = new List<Entry>(topK);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (pinnedSkillIds is { Count: > 0 })
        {
            foreach (var pin in pinnedSkillIds.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
            {
                if (result.Count >= topK) break;
                var e = _entries.Find(x =>
                    string.Equals(x.SkillId, pin, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(x.SanitizedId, pin, StringComparison.OrdinalIgnoreCase));
                if (e != null && seen.Add(e.SkillId))
                    result.Add(e);
            }
        }

        foreach (var e in _entries)
        {
            if (result.Count >= topK) break;
            if (seen.Add(e.SkillId))
                result.Add(e);
        }

        return result;
    }

    public sealed record Entry(string SkillId, string DisplayName, string ShortDescription, string SanitizedId);
}

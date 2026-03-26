using System.Text;
using System.Text.RegularExpressions;

namespace OfficeCopilot.Server.Services.SkillVm;

/// <summary>按 manifest 从技能目录解析各段 Markdown 正文。</summary>
public static class SkillVmSegmentContent
{
    /// <summary>解析段正文：优先 <c>segments/&lt;id&gt;.md</c>，否则在 <paramref name="skillMdBody"/> 中按 <c>## segmentId</c> 或 <c>## 段 &lt;id&gt;</c> 切分。</summary>
    public static string? GetSegmentText(string skillBaseDir, string segmentId, string skillMdBody, SkillVmManifest manifest)
    {
        var seg = manifest.Segments.FirstOrDefault(s => string.Equals(s.Id, segmentId, StringComparison.OrdinalIgnoreCase));
        if (seg == null) return null;

        if (!string.IsNullOrWhiteSpace(seg.Source))
        {
            var path = Path.Combine(skillBaseDir, seg.Source.Trim().Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(path))
                return File.ReadAllText(path, Encoding.UTF8);
        }

        var segmentsDir = Path.Combine(skillBaseDir, "segments", segmentId + ".md");
        if (File.Exists(segmentsDir))
            return File.ReadAllText(segmentsDir, Encoding.UTF8);

        return ExtractSegmentFromBody(skillMdBody, segmentId);
    }

    internal static string? ExtractSegmentFromBody(string body, string segmentId)
    {
        if (string.IsNullOrWhiteSpace(body)) return null;
        // ## segmentId 或 ## 段 xxx（id 匹配）
        var esc = Regex.Escape(segmentId);
        var re = new Regex(
            @"^##\s*(?:" + esc + @"|段\s*" + esc + @")\s*$",
            RegexOptions.Multiline | RegexOptions.IgnoreCase);
        var m = re.Match(body);
        if (!m.Success) return null;
        var start = m.Index;
        var after = body.IndexOf('\n', start);
        if (after < 0) after = start;
        var rest = body[(after + 1)..];
        var next = Regex.Match(rest, @"^##\s+", RegexOptions.Multiline);
        var end = next.Success ? next.Index : rest.Length;
        return rest[..end].Trim();
    }

    /// <summary>按 order 排序后的第一个段 id。</summary>
    public static string? GetFirstSegmentId(SkillVmManifest manifest)
    {
        var ordered = manifest.Segments.OrderBy(s => s.Order).ThenBy(s => s.Id, StringComparer.OrdinalIgnoreCase).ToList();
        return ordered.Count > 0 ? ordered[0].Id : null;
    }

    /// <summary>当前段在 order 序列中的下一个段 id；无则 null。</summary>
    public static string? GetNextSegmentId(SkillVmManifest manifest, string currentSegmentId)
    {
        var ordered = manifest.Segments.OrderBy(s => s.Order).ThenBy(s => s.Id, StringComparer.OrdinalIgnoreCase).ToList();
        for (var i = 0; i < ordered.Count; i++)
        {
            if (!string.Equals(ordered[i].Id, currentSegmentId, StringComparison.OrdinalIgnoreCase)) continue;
            return i + 1 < ordered.Count ? ordered[i + 1].Id : null;
        }
        return null;
    }
}

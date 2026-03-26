using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using OfficeCopilot.Server.Services.SkillVm;

namespace SkillDebugger.Cli;

/// <summary>从磁盘加载的脚本化技能集合（供 CLI 无宿主推进）。</summary>
public sealed class SkillDebuggerSkillStore
{
    private readonly Dictionary<string, SkillInfo> _byId = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyDictionary<string, SkillInfo> ById => _byId;

    public static SkillDebuggerSkillStore Load(string primarySkillDir, IReadOnlyList<string>? extraDirs = null)
    {
        var store = new SkillDebuggerSkillStore();
        store.AddSkillDir(primarySkillDir);
        if (extraDirs != null)
        {
            foreach (var d in extraDirs)
            {
                if (!string.IsNullOrWhiteSpace(d))
                    store.AddSkillDir(d);
            }
        }
        return store;
    }

    private void AddSkillDir(string dir)
    {
        dir = Path.GetFullPath(dir);
        if (!Directory.Exists(dir))
            throw new DirectoryNotFoundException(dir);
        var manifestPath = Path.Combine(dir, "skill.manifest.json");
        var m = SkillVmManifestLoader.TryLoad(manifestPath, NullLogger.Instance);
        if (m == null)
            throw new InvalidOperationException("未找到有效的 skill.manifest.json：" + manifestPath);

        var skillMd = Path.Combine(dir, "SKILL.md");
        var body = File.Exists(skillMd) ? File.ReadAllText(skillMd, Encoding.UTF8) : "";
        var id = string.IsNullOrWhiteSpace(m.SkillId) ? Path.GetFileName(dir.TrimEnd(Path.DirectorySeparatorChar)) : m.SkillId.Trim();
        if (string.IsNullOrEmpty(id))
            throw new InvalidOperationException("无法确定技能 id：" + dir);
        m.SkillId = id;

        var info = new SkillInfo(id, dir, m, body);
        _byId[id] = info;
    }

    public SkillInfo? TryGet(string skillId)
    {
        if (string.IsNullOrWhiteSpace(skillId)) return null;
        return _byId.GetValueOrDefault(skillId.Trim());
    }
}

public sealed class SkillInfo
{
    public SkillInfo(string id, string baseDir, SkillVmManifest manifest, string skillMdBody)
    {
        Id = id;
        BaseDir = baseDir;
        Manifest = manifest;
        SkillMdBody = skillMdBody;
    }

    public string Id { get; }
    public string BaseDir { get; }
    public SkillVmManifest Manifest { get; }
    public string SkillMdBody { get; }

    public string? GetSegmentText(string segmentId) =>
        SkillVmSegmentContent.GetSegmentText(BaseDir, segmentId, SkillMdBody, Manifest);
}

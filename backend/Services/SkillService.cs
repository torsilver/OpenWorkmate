using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace OfficeCopilot.Server.Services;

public class SkillDefinition
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string PromptTemplate { get; set; } = "";
    /// <summary>是否启用；停用后不会注册到 Kernel。</summary>
    public bool Enabled { get; set; } = true;
}

public sealed class SkillService
{
    private readonly string _skillsDir;
    private readonly ILogger<SkillService> _logger;
    private readonly object _lock = new();

    public event Action? OnSkillsChanged;

    public SkillService(ILogger<SkillService> logger)
    {
        _logger = logger;
        _skillsDir = Path.Combine(AppContext.BaseDirectory, "Skills");
        if (!Directory.Exists(_skillsDir))
        {
            Directory.CreateDirectory(_skillsDir);
        }
    }

    public List<SkillDefinition> GetAllSkills()
    {
        lock (_lock)
        {
            var byId = new Dictionary<string, SkillDefinition>(StringComparer.OrdinalIgnoreCase);

            // 1. Agent Skills: 先加载所有 */SKILL.md
            foreach (var dir in Directory.GetDirectories(_skillsDir))
            {
                var skillMd = Path.Combine(dir, "SKILL.md");
                if (!File.Exists(skillMd)) continue;
                try
                {
                    var skill = ParseSkillMd(skillMd, Path.GetFileName(dir));
                    if (skill != null && !string.IsNullOrEmpty(skill.Id))
                    {
                        byId[skill.Id] = skill;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to load SKILL.md {File}", skillMd);
                }
            }

            // 2. 旧格式: *.json，同 Id 不覆盖（SKILL 优先）
            foreach (var file in Directory.GetFiles(_skillsDir, "*.json"))
            {
                try
                {
                    var json = File.ReadAllText(file);
                    var skill = JsonSerializer.Deserialize<SkillDefinition>(json, JsonCtx.Default.SkillDefinition);
                    if (skill != null)
                    {
                        var idFromFileName = Path.GetFileNameWithoutExtension(file);
                        if (string.IsNullOrEmpty(skill.Id)) skill.Id = idFromFileName;
                        if (string.IsNullOrEmpty(skill.Id)) continue;
                        if (!byId.ContainsKey(skill.Id))
                            byId[skill.Id] = skill;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to load skill file {File}", file);
                }
            }

            return byId.Values.OrderBy(s => s.Id).ToList();
        }
    }

    /// <summary>解析 Agent Skills 格式的 SKILL.md：YAML frontmatter + Markdown body。</summary>
    private static SkillDefinition? ParseSkillMd(string skillMdPath, string defaultId)
    {
        var raw = File.ReadAllText(skillMdPath, Encoding.UTF8);
        var match = Regex.Match(raw, @"^\s*---\s*\r?\n(.*?)\r?\n---\s*\r?\n([\s\S]*)", RegexOptions.Singleline);
        string body;
        var name = defaultId;
        var description = "";
        var title = "";
        var enabled = true;

        if (match.Success)
        {
            var frontmatter = match.Groups[1].Value;
            body = match.Groups[2].Value.Trim();
            foreach (var line in frontmatter.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var colon = line.IndexOf(':');
                if (colon <= 0) continue;
                var key = line[..colon].Trim().ToLowerInvariant();
                var value = line[(colon + 1)..].Trim();
                if (value.StartsWith('"') && value.EndsWith('"') && value.Length >= 2)
                    value = value[1..^1].Replace("\\\"", "\"");
                if (key == "name") name = value;
                else if (key == "description") description = value;
                else if (key == "title") title = value;
                else if (key == "enabled") enabled = value.Trim().ToLowerInvariant() is "true" or "1" or "yes";
            }
        }
        else
        {
            body = raw.Trim();
        }


        if (string.IsNullOrWhiteSpace(name)) return null;

        return new SkillDefinition
        {
            Id = name,
            Name = string.IsNullOrWhiteSpace(title) ? name : title,
            Description = description,
            PromptTemplate = body,
            Enabled = enabled
        };
    }

    public void SaveSkill(SkillDefinition skill)
    {
        lock (_lock)
        {
            if (string.IsNullOrEmpty(skill.Id))
                skill.Id = Guid.NewGuid().ToString("N");

            var safeId = SanitizeId(skill.Id);
            var dir = Path.Combine(_skillsDir, safeId);
            Directory.CreateDirectory(dir);

            var sb = new StringBuilder();
            sb.AppendLine("---");
            sb.AppendLine("name: " + skill.Id);
            sb.AppendLine("description: " + EscapeYamlValue(skill.Description));
            if (!string.IsNullOrEmpty(skill.Name) && skill.Name != skill.Id)
                sb.AppendLine("title: " + EscapeYamlValue(skill.Name));
            sb.AppendLine("enabled: " + (skill.Enabled ? "true" : "false"));
            sb.AppendLine("---");
            sb.AppendLine();
            sb.Append(skill.PromptTemplate);

            var skillMdPath = Path.Combine(dir, "SKILL.md");
            File.WriteAllText(skillMdPath, sb.ToString(), Encoding.UTF8);
            _logger.LogInformation("Saved skill {Name} to {Path}", skill.Name, skillMdPath);

            var jsonPath = Path.Combine(_skillsDir, safeId + ".json");
            if (File.Exists(jsonPath))
            {
                File.Delete(jsonPath);
                _logger.LogInformation("Removed legacy JSON {Path}", jsonPath);
            }

            OnSkillsChanged?.Invoke();
        }
    }

    private static string EscapeYamlValue(string value)
    {
        if (string.IsNullOrEmpty(value)) return "\"\"";
        if (value.Contains('\n') || value.Contains('"') || value.Contains('#'))
            return "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", "").Replace("\n", "\\n") + "\"";
        return value;
    }

    public void DeleteSkill(string id)
    {
        lock (_lock)
        {
            var safeId = SanitizeId(id);
            var dir = Path.Combine(_skillsDir, safeId);
            if (Directory.Exists(dir))
            {
                try
                {
                    Directory.Delete(dir, true);
                    _logger.LogInformation("Deleted skill dir {Path}", dir);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to delete skill dir {Path}", dir);
                }
            }

            var jsonPath = Path.Combine(_skillsDir, safeId + ".json");
            if (File.Exists(jsonPath))
            {
                File.Delete(jsonPath);
                _logger.LogInformation("Deleted skill file {Path}", jsonPath);
            }

            OnSkillsChanged?.Invoke();
        }
    }

    private static string SanitizeId(string id)
    {
        return Path.GetInvalidFileNameChars().Aggregate(id.Trim(), (current, c) => current.Replace(c.ToString(), ""));
    }
}

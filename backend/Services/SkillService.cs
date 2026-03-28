using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace OfficeCopilot.Server.Services;

/// <summary>DELETE /api/skills/{id} 的结果，供 MapDelete 返回正确 HTTP 状态与 message。</summary>
public readonly record struct SkillDeleteResult(bool Ok, string? Message, int HttpStatus = 200)
{
    public static SkillDeleteResult Success() => new(true, null, 200);

    public static SkillDeleteResult BadRequest(string message) => new(false, message, 400);

    public static SkillDeleteResult NotFound(string message) => new(false, message, 404);

    public static SkillDeleteResult Conflict(string message) => new(false, message, 409);
}

public class SkillDefinition
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string PromptTemplate { get; set; } = "";
    /// <summary>是否启用；停用后不会注册到 Kernel。</summary>
    public bool Enabled { get; set; } = true;
    /// <summary>技能所在目录的完整路径，供可执行技能定位 scripts/ 等。</summary>
    public string BaseDir { get; set; } = "";
    /// <summary>Clawhub metadata：依赖的可执行程序，如 ["node"]。</summary>
    public List<string> RequiresBins { get; set; } = new();
    /// <summary>Clawhub metadata：依赖的环境变量，如 ["TAVILY_API_KEY"]。</summary>
    public List<string> RequiresEnv { get; set; } = new();
    /// <summary>主环境变量 key，用于配置展示/校验。</summary>
    public string? PrimaryEnv { get; set; }
    /// <summary>是否为可执行技能（有 requires.bins 或 scripts）。</summary>
    public bool IsExecutable { get; set; }
}

public sealed class SkillService
{
    private readonly string _skillsDir;
    private readonly ILogger<SkillService> _logger;
    private readonly object _lock = new();
    private bool _returnEmptyForToolIndexBuild;

    public event Action? OnSkillsChanged;

    /// <summary>仅用于 --build-tool-index 模式：为 true 时 GetAllSkills() 返回空列表，使 Kernel 只含内置插件。</summary>
    public void SetReturnEmptySkillsForToolIndexBuild(bool value) => _returnEmptyForToolIndexBuild = value;

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
        if (_returnEmptyForToolIndexBuild)
            return new List<SkillDefinition>();
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
                    var skill = ParseSkillMd(skillMd, Path.GetFileName(dir), dir);
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

    /// <summary>解析 Agent Skills 格式的 SKILL.md 文本：YAML frontmatter + Markdown body；支持 Clawhub metadata。</summary>
    /// <param name="scriptsBaseDir">技能目录路径；为 null 时不检查 scripts 子目录（仅依据 requiresBins 判断 IsExecutable）。</param>
    internal static SkillDefinition? ParseSkillMdFromContent(string raw, string defaultId, string? scriptsBaseDir)
    {
        var match = Regex.Match(raw, @"^\s*---\s*\r?\n(.*?)\r?\n---\s*\r?\n([\s\S]*)", RegexOptions.Singleline);
        string body;
        var name = defaultId;
        var description = "";
        var title = "";
        var enabled = true;
        var requiresBins = new List<string>();
        var requiresEnv = new List<string>();
        string? primaryEnv = null;

        if (match.Success)
        {
            var frontmatter = match.Groups[1].Value;
            body = match.Groups[2].Value.Trim();
            var lines = frontmatter.Split(new[] { '\r', '\n' }, StringSplitOptions.None);
            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
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
                else if (key == "metadata")
                {
                    if (string.IsNullOrEmpty(value))
                    {
                        var metadataLines = new List<string>();
                        for (var j = i + 1; j < lines.Length; j++)
                        {
                            var next = lines[j];
                            if (string.IsNullOrWhiteSpace(next)) break;
                            if (next.Length > 0 && (next[0] == ' ' || next[0] == '\t'))
                                metadataLines.Add(next);
                            else
                                break;
                        }
                        value = metadataLines.Count > 0 ? string.Join("\n", metadataLines) : value;
                    }
                    ParseClawhubMetadata(value, requiresBins, requiresEnv, ref primaryEnv);
                }
            }
        }
        else
        {
            body = raw.Trim();
        }

        var hasScriptsDir = !string.IsNullOrEmpty(scriptsBaseDir) && Directory.Exists(Path.Combine(scriptsBaseDir, "scripts"));
        var isExecutable = requiresBins.Count > 0 || hasScriptsDir;

        if (string.IsNullOrWhiteSpace(name)) return null;

        return new SkillDefinition
        {
            Id = name,
            Name = string.IsNullOrWhiteSpace(title) ? name : title,
            Description = description,
            PromptTemplate = body,
            Enabled = enabled,
            BaseDir = scriptsBaseDir ?? "",
            RequiresBins = requiresBins,
            RequiresEnv = requiresEnv,
            PrimaryEnv = primaryEnv,
            IsExecutable = isExecutable
        };
    }

    /// <summary>解析 Agent Skills 格式的 SKILL.md：YAML frontmatter + Markdown body；支持 Clawhub metadata。</summary>
    private static SkillDefinition? ParseSkillMd(string skillMdPath, string defaultId, string baseDir)
    {
        var raw = File.ReadAllText(skillMdPath, Encoding.UTF8);
        return ParseSkillMdFromContent(raw, defaultId, baseDir);
    }

    /// <summary>从完整 SKILL.md 文本解析技能；用于对话中生成/保存技能。要求含有效 frontmatter、非空 name 与 description。</summary>
    public bool TryParseSkillMarkdown(string raw, [NotNullWhen(true)] out SkillDefinition? skill, out string? errorMessage)
    {
        skill = null;
        errorMessage = null;
        if (string.IsNullOrWhiteSpace(raw))
        {
            errorMessage = "[技能解析失败] 内容为空。";
            return false;
        }

        if (!Regex.Match(raw, @"^\s*---\s*\r?\n(.*?)\r?\n---\s*\r?\n", RegexOptions.Singleline).Success)
        {
            errorMessage = "[技能解析失败] 必须包含 YAML frontmatter（以 --- 开头与结束的元数据块），且正文在第二个 --- 之后。";
            return false;
        }

        var parsed = ParseSkillMdFromContent(raw, "", null);
        if (parsed == null || string.IsNullOrWhiteSpace(parsed.Id))
        {
            errorMessage = "[技能解析失败] frontmatter 中缺少有效的 name。";
            return false;
        }

        if (string.IsNullOrWhiteSpace(parsed.Description))
        {
            errorMessage = "[技能解析失败] frontmatter 中 description 不能为空。";
            return false;
        }

        if (string.IsNullOrWhiteSpace(parsed.PromptTemplate))
        {
            errorMessage = "[技能解析失败] 技能正文（Markdown 说明）不能为空。";
            return false;
        }

        var safe = SanitizeId(parsed.Id);
        if (string.IsNullOrWhiteSpace(safe))
        {
            errorMessage = "[技能解析失败] name 经规范化后无效，请使用字母、数字、下划线或连字符等合法文件名字符。";
            return false;
        }

        skill = parsed;
        skill.Id = safe;
        return true;
    }

    /// <summary>是否已存在同名技能目录（含 SKILL.md）。</summary>
    public bool SkillExists(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return false;
        var safe = SanitizeId(id);
        lock (_lock)
        {
            var dir = Path.Combine(_skillsDir, safe);
            return Directory.Exists(dir) && File.Exists(Path.Combine(dir, "SKILL.md"));
        }
    }

    /// <summary>解析 Clawhub metadata（JSON 或 YAML 风格）：clawdbot.requires.bins / env、primaryEnv。</summary>
    private static void ParseClawhubMetadata(string metadataJson, List<string> requiresBins, List<string> requiresEnv, ref string? primaryEnv)
    {
        try
        {
            using var doc = JsonDocument.Parse(metadataJson);
            if (!doc.RootElement.TryGetProperty("clawdbot", out var clawdbot)) return;
            if (!clawdbot.TryGetProperty("requires", out var requires)) return;
            if (requires.TryGetProperty("bins", out var bins) && bins.ValueKind == JsonValueKind.Array)
            {
                foreach (var b in bins.EnumerateArray())
                    if (b.GetString() is { } s && !string.IsNullOrEmpty(s))
                        requiresBins.Add(s);
            }
            if (requires.TryGetProperty("anyBins", out var anyBins) && anyBins.ValueKind == JsonValueKind.Array)
            {
                foreach (var b in anyBins.EnumerateArray())
                    if (b.GetString() is { } s && !string.IsNullOrEmpty(s) && !requiresBins.Contains(s))
                        requiresBins.Add(s);
            }
            if (requires.TryGetProperty("env", out var env) && env.ValueKind == JsonValueKind.Array)
            {
                foreach (var e in env.EnumerateArray())
                    if (e.GetString() is { } s && !string.IsNullOrEmpty(s))
                        requiresEnv.Add(s);
            }
            if (clawdbot.TryGetProperty("primaryEnv", out var pe))
                primaryEnv = pe.GetString();
        }
        catch
        {
            // JSON 解析失败时尝试从 YAML 风格块中提取 env（如 microsoft-excel 等 Clawhub 技能）
            ParseClawhubMetadataYamlStyle(metadataJson, requiresBins, requiresEnv, ref primaryEnv);
        }
    }

    /// <summary>从 YAML 风格 metadata 块中提取 requires.env（env: 后的 - VAR_NAME 行）。</summary>
    private static void ParseClawhubMetadataYamlStyle(string block, List<string> requiresBins, List<string> requiresEnv, ref string? primaryEnv)
    {
        if (string.IsNullOrWhiteSpace(block)) return;
        var envIdx = block.IndexOf("env:", StringComparison.OrdinalIgnoreCase);
        var searchStart = envIdx >= 0 ? envIdx : 0;
        foreach (Match m in Regex.Matches(block[searchStart..], @"-\s*([A-Za-z_][A-Za-z0-9_]*)"))
        {
            var s = m.Groups[1].Value;
            if (!string.IsNullOrEmpty(s) && !requiresEnv.Contains(s))
                requiresEnv.Add(s);
        }
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

    public SkillDeleteResult DeleteSkill(string id)
    {
        lock (_lock)
        {
            if (string.IsNullOrWhiteSpace(id))
                return SkillDeleteResult.BadRequest("请求参数无效：技能 id 不能为空。");

            var safeId = SanitizeId(id);
            if (string.IsNullOrEmpty(safeId))
                return SkillDeleteResult.BadRequest("请求参数无效：技能 id 无效。");

            var dir = Path.Combine(_skillsDir, safeId);
            var jsonPath = Path.Combine(_skillsDir, safeId + ".json");
            var hadDir = Directory.Exists(dir);
            var hadJson = File.Exists(jsonPath);
            if (!hadDir && !hadJson)
                return SkillDeleteResult.NotFound("未找到该技能。");

            if (hadDir)
            {
                try
                {
                    Directory.Delete(dir, true);
                    _logger.LogInformation("Deleted skill dir {Path}", dir);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to delete skill dir {Path}", dir);
                    return SkillDeleteResult.Conflict(
                        "删除技能目录失败（可能被其它程序占用或权限不足）。请关闭占用该文件夹的程序后重试。");
                }

                if (Directory.Exists(dir))
                {
                    return SkillDeleteResult.Conflict(
                        "删除技能目录失败（删除后目录仍存在，可能被占用）。请关闭占用该文件夹的程序后重试。");
                }
            }

            if (File.Exists(jsonPath))
            {
                try
                {
                    File.Delete(jsonPath);
                    _logger.LogInformation("Deleted skill file {Path}", jsonPath);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to delete skill file {Path}", jsonPath);
                    return SkillDeleteResult.Conflict(
                        "删除技能文件失败（可能被占用或权限不足）。请关闭占用后重试。");
                }
            }

            OnSkillsChanged?.Invoke();
            return SkillDeleteResult.Success();
        }
    }

    private static string SanitizeId(string id)
    {
        return Path.GetInvalidFileNameChars().Aggregate(id.Trim(), (current, c) => current.Replace(c.ToString(), ""));
    }
}

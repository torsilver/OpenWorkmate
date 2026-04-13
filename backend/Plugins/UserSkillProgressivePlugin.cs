using System.ComponentModel;
using System.Globalization;
using System.Text;
using OfficeCopilot.Server.Services;
using OfficeCopilot.Server.Services.DynamicTooling;

namespace OfficeCopilot.Server.Plugins;

/// <summary>渐进式用户技能：元数据由 system 块提供；正文与附属文件经本插件按需读盘，与业务工具检索分离。</summary>
[CopilotPluginId("UserSkillProgressive")]
public sealed class UserSkillProgressivePlugin
{
    private const int MaxInstructionChars = 65536;
    private const int MaxResourceBytes = 512 * 1024;

    private readonly SkillService _skillService;
    private readonly ILogger<UserSkillProgressivePlugin> _logger;

    public UserSkillProgressivePlugin(SkillService skillService, ILogger<UserSkillProgressivePlugin> logger)
    {
        _skillService = skillService;
        _logger = logger;
    }

    [ToolFunction(DynamicToolingConstants.LoadUserSkillInstructionsFunctionName)]
    [Description(
        "按需加载用户技能（SKILL）的正文或技能目录下的附属文件。与 search_available_tools 无关：技能清单见 system 中的「渐进式用户技能」块。"
        + " skillId 填技能 Id（frontmatter name）或与其等价的消毒函数名。"
        + " 不传 relativeResourcePath 时返回 SKILL.md 正文（第二个 --- 之后）；传入时返回该相对路径下的文本文件片段（须在技能 BaseDir 内）。")]
    public Task<string> LoadUserSkillInstructionsAsync(
        [Description("技能 Id（与设置中一致）或消毒后的函数名")] string skillId,
        [Description("可选：相对技能目录的路径，如 references/foo.md；留空则加载 SKILL.md 正文")] string? relativeResourcePath = null,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var idInput = (skillId ?? "").Trim();
        if (idInput.Length == 0)
            return Task.FromResult("[load_user_skill_instructions] 错误：skillId 不能为空。");

        var skill = ResolveEnabledSkill(idInput);
        if (skill == null)
        {
            _logger.LogInformation("[SkillProgressive] unknown or disabled skillId={SkillId}", idInput);
            return Task.FromResult(
                "[load_user_skill_instructions] 未找到已启用的技能，或 skillId 不匹配。请对照 system 中「渐进式用户技能」列表的 Id。");
        }

        var rel = (relativeResourcePath ?? "").Trim();
        try
        {
            if (rel.Length == 0)
            {
                var body = TryGetSkillMarkdownBody(skill, out var err);
                if (body == null)
                    return Task.FromResult("[load_user_skill_instructions] " + err);
                var truncated = TruncateWithNotice(body, MaxInstructionChars);
                _logger.LogInformation("[SkillProgressive] loaded SKILL body skillId={SkillId} chars={Chars}", skill.Id, truncated.Length);
                return Task.FromResult("[load_user_skill_instructions] 以下为技能「" + skill.Id + "」的正文：\n\n" + truncated);
            }

            if (!TryResolveSafeResourcePath(skill, rel, out var fullPath, out var pathErr))
            {
                _logger.LogInformation("[SkillProgressive] bad resource path skillId={SkillId} rel={Rel} err={Err}", skill.Id, rel, pathErr);
                return Task.FromResult("[load_user_skill_instructions] " + pathErr);
            }

            var fi = new FileInfo(fullPath);
            if (!fi.Exists)
                return Task.FromResult("[load_user_skill_instructions] 文件不存在：" + rel);

            if (fi.Length > MaxResourceBytes)
            {
                return Task.FromResult(
                    $"[load_user_skill_instructions] 文件过大（{fi.Length} 字节），上限 {MaxResourceBytes} 字节。请换更小文件或分段读取。");
            }

            var bytes = File.ReadAllBytes(fullPath);
            var text = Encoding.UTF8.GetString(bytes);
            var trunc = TruncateWithNotice(text, MaxInstructionChars);
            _logger.LogInformation("[SkillProgressive] loaded resource skillId={SkillId} rel={Rel} chars={Chars}", skill.Id, rel, trunc.Length);
            return Task.FromResult("[load_user_skill_instructions] 文件 `" + rel + "` 内容：\n\n" + trunc);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[SkillProgressive] read failed skillId={SkillId}", skill.Id);
            return Task.FromResult("[load_user_skill_instructions] 读取失败：" + ex.Message);
        }
    }

    private SkillDefinition? ResolveEnabledSkill(string idInput)
    {
        foreach (var s in _skillService.GetAllSkills())
        {
            if (!s.Enabled) continue;
            if (string.Equals(s.Id, idInput, StringComparison.OrdinalIgnoreCase))
                return s;
            var sanitized = UserSkillToolNaming.SanitizeToFunctionName(s.Id);
            if (string.Equals(sanitized, idInput, StringComparison.OrdinalIgnoreCase))
                return s;
        }

        return null;
    }

    private static string? TryGetSkillMarkdownBody(SkillDefinition skill, out string error)
    {
        error = "";
        var baseDir = (skill.BaseDir ?? "").Trim();
        if (baseDir.Length > 0)
        {
            var md = Path.Combine(baseDir, "SKILL.md");
            if (File.Exists(md))
            {
                try
                {
                    var raw = File.ReadAllText(md, Encoding.UTF8);
                    var body = SkillService.ExtractMarkdownBodyAfterFrontmatterFromRaw(raw).Trim();
                    if (body.Length > 0)
                        return body;
                    error = "SKILL.md 正文为空。";
                    return null;
                }
                catch (Exception ex)
                {
                    error = "读取 SKILL.md 失败：" + ex.Message;
                    return null;
                }
            }
        }

        var mem = (skill.PromptTemplate ?? "").Trim();
        if (mem.Length > 0)
            return mem;

        error = "该技能无 SKILL.md 文件且无内存正文（PromptTemplate）。";
        return null;
    }

    /// <summary>将 relative 解析为技能目录下的绝对路径；禁止 .. 与盘符根逃逸。</summary>
    internal static bool TryResolveSafeResourcePath(SkillDefinition skill, string relative, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out string? fullPath, out string errorMessage)
    {
        fullPath = null;
        errorMessage = "";
        var baseDir = (skill.BaseDir ?? "").Trim();
        if (baseDir.Length == 0 || !Directory.Exists(baseDir))
        {
            errorMessage = "该技能无有效 BaseDir，无法加载附属文件。";
            return false;
        }

        if (relative.Contains("..", StringComparison.Ordinal) || Path.IsPathRooted(relative))
        {
            errorMessage = "relativeResourcePath 不允许包含 .. 或为绝对路径。";
            return false;
        }

        var normalizedRel = relative.Replace('/', Path.DirectorySeparatorChar).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (string.Equals(Path.GetFileName(normalizedRel), "SKILL.md", StringComparison.OrdinalIgnoreCase))
        {
            errorMessage = "加载 SKILL.md 正文请勿传 relativeResourcePath，只传 skillId。";
            return false;
        }

        string baseFull;
        string candidateFull;
        try
        {
            baseFull = Path.GetFullPath(baseDir);
            candidateFull = Path.GetFullPath(Path.Combine(baseFull, normalizedRel));
        }
        catch
        {
            errorMessage = "路径无效。";
            return false;
        }

        string relToBase;
        try
        {
            relToBase = Path.GetRelativePath(baseFull, candidateFull);
        }
        catch
        {
            errorMessage = "路径无效。";
            return false;
        }

        if (string.Equals(relToBase, "..", StringComparison.Ordinal)
            || relToBase.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            || relToBase.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal))
        {
            errorMessage = "路径越出技能目录。";
            return false;
        }

        fullPath = candidateFull;
        return true;
    }

    private static string TruncateWithNotice(string text, int maxChars)
    {
        if (text.Length <= maxChars)
            return text;
        return text[..maxChars] + "\n\n[已截断：原文超过 " + maxChars.ToString(CultureInfo.InvariantCulture) + " 字符。]";
    }
}

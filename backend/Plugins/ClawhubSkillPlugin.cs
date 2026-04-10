using System.ComponentModel;
using OfficeCopilot.Server;
using OfficeCopilot.Server.Services;

namespace OfficeCopilot.Server.Plugins;

/// <summary>
/// 为 Clawhub 可执行技能提供通用脚本执行：根据技能 BaseDir 与 scripts/ 下的脚本名执行 node，并注入技能所需环境变量。
/// 适用于尚无原生适配器的可执行技能。
/// </summary>
[CopilotPluginId("ClawhubSkill")]
public sealed class ClawhubSkillPlugin
{
    private readonly SkillService _skillService;
    private readonly ClawhubScriptRunner _runner;
    private readonly ConfigService _configService;
    private readonly ILogger<ClawhubSkillPlugin>? _logger;

    public ClawhubSkillPlugin(SkillService skillService, ClawhubScriptRunner runner, ConfigService configService, ILogger<ClawhubSkillPlugin>? logger = null)
    {
        _skillService = skillService;
        _runner = runner;
        _configService = configService;
        _logger = logger;
    }

    [ToolFunction("run_clawhub_script")]
    [Description("在后端本机 Node 下运行技能目录 scripts/ 中的脚本（.mjs 优先，否则 .js）。scriptName 不含扩展名；arguments 按空格切分，含空格的参数须用双引号包裹。")]
    public async Task<string> RunClawhubScriptAsync(
        [Description("技能 ID，与 SKILL.md 中 name 一致")] string skillId,
        [Description("脚本名（不含扩展名），如 search 或 extract")] string scriptName,
        [Description("传给脚本的参数：空格分隔；含空格的值用双引号包起来，例如 search \"量子计算\" zh。若失败，根据返回中的 stderr 调整参数或检查技能 RequiresEnv")] string arguments = "")
    {
        if (string.IsNullOrWhiteSpace(skillId))
            return "[错误] 请提供技能 ID。";
        if (string.IsNullOrWhiteSpace(scriptName))
            return "[错误] 请提供脚本名（如 search、extract）。";

        var skills = _skillService.GetAllSkills();
        var skill = skills.FirstOrDefault(s => string.Equals(s.Id, skillId, StringComparison.OrdinalIgnoreCase));
        if (skill == null)
            return $"[错误] 未找到技能: {skillId}。";
        if (!skill.Enabled)
            return $"[错误] 技能 {skillId} 已在设置中停用，无法通过 run_clawhub_script 执行；请先在设置页「技能与 MCP → 自定义技能」中启用该技能。";
        if (string.IsNullOrWhiteSpace(skill.BaseDir) || !skill.IsExecutable)
            return $"[错误] 技能 {skillId} 不是可执行技能或缺少 BaseDir。";

        var scriptPath = "scripts/" + scriptName.Trim() + ".mjs";
        var scriptPathJs = "scripts/" + scriptName.Trim() + ".js";
        var baseDir = skill.BaseDir;
        var hasMjs = File.Exists(Path.Combine(baseDir, scriptPath));
        var hasJs = File.Exists(Path.Combine(baseDir, scriptPathJs));
        if (!hasMjs && !hasJs)
            return $"[错误] 脚本不存在: {scriptPath} 或 {scriptPathJs}。";
        if (!hasMjs) scriptPath = scriptPathJs;

        var args = SplitArguments(arguments);
        var env = new Dictionary<string, string>();
        var skillEnv = _configService.Current.SkillEnv;
        foreach (var key in skill.RequiresEnv)
        {
            if (string.IsNullOrEmpty(key)) continue;
            string? value = skillEnv != null && skillEnv.TryGetValue(key, out var v) && !string.IsNullOrEmpty(v)
                ? v
                : null;
            if (value == null)
                value = Environment.GetEnvironmentVariable(key);
            if (!string.IsNullOrEmpty(value))
                env[key] = value;
        }

        return await _runner.RunAsync(baseDir, scriptPath, args, env);
    }

    private static List<string> SplitArguments(string arguments)
    {
        var list = new List<string>();
        if (string.IsNullOrWhiteSpace(arguments)) return list;
        var s = arguments.Trim();
        var i = 0;
        while (i < s.Length)
        {
            while (i < s.Length && (s[i] == ' ' || s[i] == '\t')) i++;
            if (i >= s.Length) break;
            if (s[i] == '"')
            {
                var end = s.IndexOf('"', i + 1);
                if (end < 0) { list.Add(s[i..]); break; }
                list.Add(s[(i + 1)..end].Replace("\\\"", "\""));
                i = end + 1;
            }
            else
            {
                var start = i;
                while (i < s.Length && s[i] != ' ' && s[i] != '\t') i++;
                list.Add(s[start..i]);
            }
        }
        return list;
    }
}

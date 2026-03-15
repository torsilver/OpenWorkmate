using System.ComponentModel;
using Microsoft.SemanticKernel;
using OfficeCopilot.Server;
using OfficeCopilot.Server.Services;

namespace OfficeCopilot.Server.Plugins;

/// <summary>
/// 为 Clawhub 可执行技能提供通用脚本执行：根据技能 BaseDir 与 scripts/ 下的脚本名执行 node，并注入技能所需环境变量。
/// 适用于尚无原生适配器的可执行技能。
/// </summary>
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

    [KernelFunction("run_clawhub_script")]
    [Description("运行位置：后端服务所在机器的 Node 进程（技能目录 scripts/ 下）。运行 Clawhub 可执行技能中的脚本。scriptName 为脚本名不含扩展名（如 search、extract），arguments 为空格分隔的参数字符串。")]
    public async Task<string> RunClawhubScriptAsync(
        [Description("技能 ID，与 SKILL.md 中 name 一致，如 tavily")] string skillId,
        [Description("脚本名（不含扩展名），如 search 或 extract")] string scriptName,
        [Description("传给脚本的参数，空格分隔，如 \"hello world\" 或 5 --deep")] string arguments = "")
    {
        if (string.IsNullOrWhiteSpace(skillId))
            return "[错误] 请提供技能 ID。";
        if (string.IsNullOrWhiteSpace(scriptName))
            return "[错误] 请提供脚本名（如 search、extract）。";

        var skills = _skillService.GetAllSkills();
        var skill = skills.FirstOrDefault(s => string.Equals(s.Id, skillId, StringComparison.OrdinalIgnoreCase));
        if (skill == null)
            return $"[错误] 未找到技能: {skillId}。";
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
        var tavilyKey = (_configService.Current.TavilyApiKey ?? "").Trim();
        if (string.IsNullOrEmpty(tavilyKey)) tavilyKey = Environment.GetEnvironmentVariable("TAVILY_API_KEY") ?? "";
        foreach (var key in skill.RequiresEnv)
        {
            if (string.IsNullOrEmpty(key)) continue;
            var value = (skillEnv != null && skillEnv.TryGetValue(key, out var v) && !string.IsNullOrEmpty(v))
                ? v
                : (string.Equals(key, "TAVILY_API_KEY", StringComparison.OrdinalIgnoreCase) ? tavilyKey : null) ?? Environment.GetEnvironmentVariable(key);
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

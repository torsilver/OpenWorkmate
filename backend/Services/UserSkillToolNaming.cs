using System.Text;

namespace OfficeCopilot.Server.Services;

/// <summary>UserSkill 注册到 ToolRegistry 时使用的插件名/函数名规则（与 ChatService 动态注册一致）。</summary>
public static class UserSkillToolNaming
{
    /// <summary>将技能 Id（如 "Excel / XLSX"）规范为工具可用的函数名（如 "Excel_XLSX"）。</summary>
    public static string SanitizeToFunctionName(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return "Skill";
        var s = id.Trim().Replace("-", "_").Replace("/", "_").Replace(" ", "_");
        var sb = new StringBuilder(s.Length);
        var prevUnderscore = false;
        foreach (var c in s)
        {
            if (char.IsLetterOrDigit(c) || c == '_')
            {
                sb.Append(c);
                prevUnderscore = false;
            }
            else if (!prevUnderscore)
            {
                sb.Append('_');
                prevUnderscore = true;
            }
        }
        var result = sb.ToString().Trim('_');
        return string.IsNullOrEmpty(result) ? "Skill" : result;
    }

    public static string PluginNameForSkillId(string skillId) => $"UserSkill_{SanitizeToFunctionName(skillId)}";
}

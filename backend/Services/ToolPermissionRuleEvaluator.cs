using System.Text.RegularExpressions;
using OfficeCopilot.Server;

namespace OfficeCopilot.Server.Services;

public enum ToolPermissionRuleEffect
{
    None,
    Deny,
    Ask,
    AllowAlways,
    AllowOnceSession
}

/// <summary>
/// 解析 <see cref="AppConfig.ToolPermissionRules"/>；多条匹配时优先级 Deny &gt; Ask &gt; AllowAlways &gt; AllowOnceSession。
/// </summary>
public static class ToolPermissionRuleEvaluator
{
    /// <summary>将规则效果应用到是否需 HITL：<c>Ask</c> 强制确认；<c>Allow*</c> 跳过确认。</summary>
    public static void ApplyToNeedHitl(ref bool needHitl, ToolPermissionRuleEffect ruleEffect)
    {
        if (ruleEffect == ToolPermissionRuleEffect.Ask)
            needHitl = true;
        else if (ruleEffect is ToolPermissionRuleEffect.AllowAlways or ToolPermissionRuleEffect.AllowOnceSession)
            needHitl = false;
    }

    /// <summary>自定义脚本等「默认必确认」场景：为 false 时表示规则允许跳过确认。</summary>
    public static bool RequiresConfirmation(ToolPermissionRuleEffect ruleEffect) =>
        ruleEffect is not (ToolPermissionRuleEffect.AllowAlways or ToolPermissionRuleEffect.AllowOnceSession);

    public static ToolPermissionRuleEffect Evaluate(IReadOnlyList<ToolPermissionRule>? rules, string? pluginName, string? functionName)
    {
        if (rules == null || rules.Count == 0)
            return ToolPermissionRuleEffect.None;
        var plugin = pluginName ?? "";
        var fn = functionName ?? "";
        var seenDeny = false;
        var seenAsk = false;
        var seenAllowAlways = false;
        var seenAllowOnce = false;
        foreach (var rule in rules)
        {
            if (rule == null || string.IsNullOrWhiteSpace(rule.Pattern))
                continue;
            if (!PatternMatches(rule.Pattern.Trim(), plugin, fn))
                continue;
            var eff = ParseEffect(rule.Effect);
            switch (eff)
            {
                case ToolPermissionRuleEffect.Deny: seenDeny = true; break;
                case ToolPermissionRuleEffect.Ask: seenAsk = true; break;
                case ToolPermissionRuleEffect.AllowAlways: seenAllowAlways = true; break;
                case ToolPermissionRuleEffect.AllowOnceSession: seenAllowOnce = true; break;
            }
        }
        if (seenDeny) return ToolPermissionRuleEffect.Deny;
        if (seenAsk) return ToolPermissionRuleEffect.Ask;
        if (seenAllowAlways) return ToolPermissionRuleEffect.AllowAlways;
        if (seenAllowOnce) return ToolPermissionRuleEffect.AllowOnceSession;
        return ToolPermissionRuleEffect.None;
    }

    private static ToolPermissionRuleEffect ParseEffect(string? effect)
    {
        var e = (effect ?? "").Trim();
        if (string.IsNullOrEmpty(e)) return ToolPermissionRuleEffect.None;
        if (string.Equals(e, "deny", StringComparison.OrdinalIgnoreCase)) return ToolPermissionRuleEffect.Deny;
        if (string.Equals(e, "ask", StringComparison.OrdinalIgnoreCase)) return ToolPermissionRuleEffect.Ask;
        if (string.Equals(e, "allowAlways", StringComparison.OrdinalIgnoreCase)) return ToolPermissionRuleEffect.AllowAlways;
        if (string.Equals(e, "allowOnceSession", StringComparison.OrdinalIgnoreCase)) return ToolPermissionRuleEffect.AllowOnceSession;
        return ToolPermissionRuleEffect.None;
    }

    private static bool PatternMatches(string pattern, string plugin, string function)
    {
        var colon = pattern.IndexOf(':');
        if (colon < 0)
            return false;
        var pPat = pattern[..colon].Trim();
        var fPat = pattern[(colon + 1)..].Trim();
        return GlobMatch(pPat, plugin) && GlobMatch(fPat, function);
    }

    private static bool GlobMatch(string pattern, string text)
    {
        if (string.IsNullOrEmpty(pattern) || pattern == "*")
            return true;
        if (!pattern.Contains('*', StringComparison.Ordinal))
            return string.Equals(pattern, text, StringComparison.OrdinalIgnoreCase);
        var escaped = Regex.Escape(pattern);
        var regex = "^" + escaped.Replace("\\*", ".*", StringComparison.Ordinal) + "$";
        return Regex.IsMatch(text, regex, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }
}

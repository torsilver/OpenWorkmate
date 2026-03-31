using System.Text.RegularExpressions;

namespace OfficeCopilot.Server.Services;

/// <summary>
/// 将模型或用户传入的附件引用规范为缓存字典键：<c>attachment:</c> + 32 位小写 hex（与 <see cref="Guid.ToString(string)"/> N 格式一致）。
/// </summary>
public static class AttachmentRefNormalizer
{
    private static readonly Regex Hex32 = new("^[a-fA-F0-9]{32}$", RegexOptions.Compiled);

    /// <summary>
    /// 返回规范键，无法识别时返回 null（空白、非 32 位 hex、attachment: 后非法等）。
    /// </summary>
    public static string? TryNormalize(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) return null;
        var t = input.Trim();
        if (t.StartsWith("attachment:", StringComparison.OrdinalIgnoreCase))
        {
            var id = t["attachment:".Length..].Trim();
            if (id.Length == 0 || !Hex32.IsMatch(id)) return null;
            return "attachment:" + id.ToLowerInvariant();
        }
        if (Hex32.IsMatch(t))
            return "attachment:" + t.ToLowerInvariant();
        return null;
    }
}

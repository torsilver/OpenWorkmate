using System.Text.Json;
using Microsoft.Extensions.Options;
using OpenWorkmate.AI.Gateway.Models;

namespace OpenWorkmate.AI.Gateway.Services;

public sealed class UserPolicyStore
{
    public const string FileName = "policy.user.json";

    private readonly IOptionsMonitor<AiGatewayOptions> _opt;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    private static readonly JsonSerializerOptions JsonRead = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    public UserPolicyStore(IOptionsMonitor<AiGatewayOptions> opt) => _opt = opt;

    public string PolicyPath => Path.Combine(Path.GetFullPath(_opt.CurrentValue.DataRoot), FileName);

    public UserPolicyFile ReadOrDefault()
    {
        try
        {
            if (File.Exists(PolicyPath))
            {
                var t = File.ReadAllText(PolicyPath);
                var u = JsonSerializer.Deserialize<UserPolicyFile>(t, JsonRead);
                if (u != null) return Normalize(u);
            }
        }
        catch
        {
            /* fall through */
        }

        return new UserPolicyFile { SchemaVersion = 1, RouteMode = "gateway" };
    }

    public void Write(UserPolicyFile policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        var root = Path.GetFullPath(_opt.CurrentValue.DataRoot);
        Directory.CreateDirectory(root);
        var normalized = Normalize(policy);
        File.WriteAllText(Path.Combine(root, FileName), JsonSerializer.Serialize(normalized, JsonOpts));
    }

    private static UserPolicyFile Normalize(UserPolicyFile u)
    {
        var m = (u.RouteMode ?? "gateway").Trim().ToLowerInvariant();
        if (m != "gateway" && m != "direct") m = "gateway";
        return new UserPolicyFile { SchemaVersion = u.SchemaVersion > 0 ? u.SchemaVersion : 1, RouteMode = m };
    }
}

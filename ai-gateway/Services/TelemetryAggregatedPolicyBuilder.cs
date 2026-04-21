using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using Taskly.AI.Gateway.Models;

namespace Taskly.AI.Gateway.Services;

public sealed class TelemetryAggregatedPolicyBuilder
{
    private static readonly JsonSerializerOptions StableJsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly TelemetryPolicyResolver _policies;
    private readonly UserPolicyStore _userPolicy;
    private readonly IOptionsMonitor<AiGatewayOptions> _opt;

    public TelemetryAggregatedPolicyBuilder(
        TelemetryPolicyResolver policies,
        UserPolicyStore userPolicy,
        IOptionsMonitor<AiGatewayOptions> opt)
    {
        _policies = policies;
        _userPolicy = userPolicy;
        _opt = opt;
    }

    public (AggregatedPolicyEnvelope Envelope, string ETagHeaderValue) BuildEnvelope(string? profileId)
    {
        var ops = _policies.GetBundleForDisplay();
        var user = _userPolicy.ReadOrDefault();
        var allowed = new HashSet<string>(_policies.GetAllowedRouteModes(), StringComparer.OrdinalIgnoreCase);
        var violations = new List<string>();
        var wantMode = (user.RouteMode ?? "gateway").Trim().ToLowerInvariant();
        if (wantMode != "gateway" && wantMode != "direct") wantMode = "gateway";
        if (!allowed.Contains(wantMode))
        {
            violations.Add($"routeMode '{user.RouteMode}' 不在运维允许列表内");
            wantMode = allowed.Contains("gateway") ? "gateway" : allowed.FirstOrDefault() ?? "gateway";
        }

        var effective = BuildEffectiveBody(profileId, wantMode);
        var etagHex = ComputeStableEtagHex(effective);
        effective.ETag = etagHex;
        var etagHeader = "\"" + etagHex + "\"";

        var envelope = new AggregatedPolicyEnvelope
        {
            SchemaVersion = 1,
            SyncedAt = DateTime.UtcNow,
            ETag = etagHex,
            Ops = ops,
            User = user,
            UserOverlayViolations = violations,
            Effective = effective
        };
        return (envelope, etagHeader);
    }

    private TelemetryAggregatedPolicyResponse BuildEffectiveBody(string? profileId, string routeMode)
    {
        var transmission = _policies.GetTransmissionPolicy();
        var profilesDoc = _policies.GetPolicyProfiles();
        var defaults = _policies.ReadDefaultsOnly();
        var maxChars = Math.Clamp(_opt.CurrentValue.MaxEventPayloadChars, 1000, 500_000);

        var selected = ResolveProfile(profilesDoc, profileId);
        var kindsList = selected?.EventKinds ?? new List<TelemetryEventKindEntry>();
        var mergedTransmission = TelemetryTransmissionPolicyDefaults.MergeOverlay(transmission, selected?.Transmission);
        mergedTransmission.Full.MsgMax = Math.Min(mergedTransmission.Full.MsgMax, maxChars);
        mergedTransmission.Full.PayloadMax = Math.Min(mergedTransmission.Full.PayloadMax, maxChars);
        var ingestLv = string.IsNullOrWhiteSpace(selected?.IngestLogLevel)
            ? "information"
            : selected!.IngestLogLevel!.Trim();

        return new TelemetryAggregatedPolicyResponse
        {
            SchemaVersion = 1,
            SyncedAt = DateTime.UtcNow,
            Transmission = mergedTransmission,
            AvailableEventKinds = kindsList.ToList(),
            PolicyProfiles = profilesDoc.Profiles.ToList(),
            DefaultPolicyProfileId = profilesDoc.DefaultProfileId,
            SelectedPolicyProfileId = selected?.Id ?? "",
            TelemetryEmissionAllowed = true,
            Defaults = defaults,
            MaxEventPayloadChars = maxChars,
            IngestLogLevel = ingestLv,
            RouteMode = routeMode
        };
    }

    private static TelemetryPolicyProfileEntry? ResolveProfile(TelemetryPolicyProfilesFile doc, string? requestedId)
    {
        if (doc.Profiles is not { Count: > 0 })
            return null;
        var def = (doc.DefaultProfileId ?? "default").Trim();
        var want = string.IsNullOrWhiteSpace(requestedId) ? def : requestedId.Trim();
        var p = doc.Profiles.FirstOrDefault(x => string.Equals(x.Id, want, StringComparison.Ordinal));
        if (p != null) return p;
        p = doc.Profiles.FirstOrDefault(x => string.Equals(x.Id, def, StringComparison.Ordinal));
        return p ?? doc.Profiles[0];
    }

    public static string ComputeStableEtagHex(TelemetryAggregatedPolicyResponse body)
    {
        var stable = new
        {
            body.SchemaVersion,
            body.Transmission,
            body.AvailableEventKinds,
            body.PolicyProfiles,
            body.DefaultPolicyProfileId,
            body.SelectedPolicyProfileId,
            body.TelemetryEmissionAllowed,
            body.IngestLogLevel,
            body.Defaults,
            body.MaxEventPayloadChars,
            body.RouteMode
        };
        var json = JsonSerializer.Serialize(stable, StableJsonOpts);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(json));
        return Convert.ToHexString(hash)[..16];
    }
}

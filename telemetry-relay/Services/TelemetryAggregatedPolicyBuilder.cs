using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using Taskly.Telemetry.Relay.Models;

namespace Taskly.Telemetry.Relay.Services;

public sealed class TelemetryAggregatedPolicyBuilder
{
    private static readonly JsonSerializerOptions StableJsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly TelemetryPolicyResolver _policies;
    private readonly IOptionsMonitor<TelemetryOptions> _opt;

    public TelemetryAggregatedPolicyBuilder(TelemetryPolicyResolver policies, IOptionsMonitor<TelemetryOptions> opt)
    {
        _policies = policies;
        _opt = opt;
    }

    public (TelemetryAggregatedPolicyResponse Body, string ETagHeaderValue) Build(string? profileId)
    {
        var transmission = _policies.GetTransmissionPolicy();
        var profilesDoc = _policies.GetPolicyProfiles();
        var defaults = _policies.ReadDefaultsOnly();
        var maxChars = Math.Clamp(_opt.CurrentValue.MaxEventPayloadChars, 1000, 500_000);

        var selected = ResolveProfile(profilesDoc, profileId);
        var kindsList = selected?.LogKinds ?? new List<TelemetryLogKindEntry>();
        var mergedTransmission = TelemetryTransmissionPolicyDefaults.MergeOverlay(transmission, selected?.Transmission);
        mergedTransmission.Full.MsgMax = Math.Min(mergedTransmission.Full.MsgMax, maxChars);
        mergedTransmission.Full.PayloadMax = Math.Min(mergedTransmission.Full.PayloadMax, maxChars);
        var ingestLv = string.IsNullOrWhiteSpace(selected?.IngestLogLevel)
            ? "information"
            : selected!.IngestLogLevel!.Trim();

        var body = new TelemetryAggregatedPolicyResponse
        {
            SchemaVersion = 1,
            SyncedAt = DateTime.UtcNow,
            Transmission = mergedTransmission,
            AvailableLogKinds = kindsList.ToList(),
            PolicyProfiles = profilesDoc.Profiles.ToList(),
            DefaultPolicyProfileId = profilesDoc.DefaultProfileId,
            SelectedPolicyProfileId = selected?.Id ?? "",
            TelemetryEmissionAllowed = true,
            Defaults = defaults,
            MaxEventPayloadChars = maxChars,
            IngestLogLevel = ingestLv
        };
        var etagHex = ComputeStableEtagHex(body);
        body.ETag = etagHex;
        var etagHeader = "\"" + etagHex + "\"";
        return (body, etagHeader);
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

    /// <summary>不含 <see cref="TelemetryAggregatedPolicyResponse.SyncedAt"/>，便于 If-None-Match。</summary>
    public static string ComputeStableEtagHex(TelemetryAggregatedPolicyResponse body)
    {
        var stable = new
        {
            body.SchemaVersion,
            body.Transmission,
            body.AvailableLogKinds,
            body.PolicyProfiles,
            body.DefaultPolicyProfileId,
            body.SelectedPolicyProfileId,
            body.TelemetryEmissionAllowed,
            body.IngestLogLevel,
            body.Defaults,
            body.MaxEventPayloadChars
        };
        var json = JsonSerializer.Serialize(stable, StableJsonOpts);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(json));
        return Convert.ToHexString(hash)[..16];
    }
}

using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace Taskly.Telemetry.Relay.Tests;

public sealed class AggregatedPolicyIntegrationTests : IClassFixture<RelayWebApplicationFactory>, IDisposable
{
    private readonly HttpClient _client;

    public AggregatedPolicyIntegrationTests(RelayWebApplicationFactory factory)
    {
        _client = factory.CreateClient();
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "test-policy-key");
    }

    public void Dispose() => _client.Dispose();

    [Fact]
    public async Task Get_policy_aggregated_returns_transmission_and_etag()
    {
        using var res = await _client.GetAsync("/policy/aggregated");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.False(string.IsNullOrEmpty(res.Headers.ETag?.Tag));
        await using var stream = await res.Content.ReadAsStreamAsync();
        var doc = await JsonSerializer.DeserializeAsync<JsonElement>(stream);
        Assert.True(doc.TryGetProperty("transmission", out var tr));
        Assert.True(tr.TryGetProperty("schemaVersion", out _));
        Assert.True(doc.TryGetProperty("syncedAt", out _));
        Assert.True(doc.TryGetProperty("etag", out _));
        Assert.True(doc.TryGetProperty("availableLogKinds", out var kinds));
        Assert.Equal(JsonValueKind.Array, kinds.ValueKind);
        Assert.True(kinds.GetArrayLength() >= 5);
        var first = kinds[0];
        Assert.True(first.TryGetProperty("kind", out var kindProp));
        Assert.Equal(JsonValueKind.String, kindProp.ValueKind);
        Assert.True(doc.TryGetProperty("telemetryEmissionAllowed", out var tea));
        Assert.Equal(JsonValueKind.True, tea.ValueKind);
        Assert.True(doc.TryGetProperty("ingestLogLevel", out var ingest));
        Assert.Equal(JsonValueKind.String, ingest.ValueKind);
    }
}

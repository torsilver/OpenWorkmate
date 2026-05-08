using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace OpenWorkmate.AI.Gateway.Tests;

public sealed class AdminPolicyBundleIntegrationTests : IClassFixture<GatewayWebApplicationFactory>, IDisposable
{
    private readonly GatewayWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public AdminPolicyBundleIntegrationTests(GatewayWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
        _client.DefaultRequestHeaders.TryAddWithoutValidation("X-Admin-Key", "test-admin-key");
    }

    public void Dispose() => _client.Dispose();

    [Fact]
    public async Task Get_api_admin_policy_returns_bundle_shape()
    {
        using var res = await _client.GetAsync("/api/admin/policy");
        res.EnsureSuccessStatusCode();
        await using var stream = await res.Content.ReadAsStreamAsync();
        var doc = await JsonSerializer.DeserializeAsync<JsonElement>(stream);
        Assert.True(doc.TryGetProperty("schemaVersion", out _));
        Assert.True(doc.TryGetProperty("transmission", out _));
        Assert.True(doc.TryGetProperty("defaults", out _));
        Assert.True(doc.TryGetProperty("policyProfiles", out var prof));
        Assert.True(prof.TryGetProperty("profiles", out var profiles));
        Assert.Equal(JsonValueKind.Array, profiles.ValueKind);
    }

    [Fact]
    public async Task Put_api_admin_policy_invalid_json_returns_400()
    {
        using var res = await _client.PutAsync(
            "/api/admin/policy",
            new StringContent("{ not json", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        var text = await res.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(text);
        Assert.False(doc.RootElement.GetProperty("ok").GetBoolean());
        Assert.False(string.IsNullOrWhiteSpace(doc.RootElement.GetProperty("message").GetString()));
    }

    [Fact]
    public async Task Put_api_admin_policy_roundtrip_then_aggregated_ok()
    {
        using var getRes = await _client.GetAsync("/api/admin/policy");
        getRes.EnsureSuccessStatusCode();
        var bundleJson = await getRes.Content.ReadAsStringAsync();

        using var putRes = await _client.PutAsync(
            "/api/admin/policy",
            new StringContent(bundleJson, Encoding.UTF8, "application/json"));
        putRes.EnsureSuccessStatusCode();

        using var policyClient = _factory.CreateClient();
        policyClient.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", "test-policy-key");
        using var agg = await policyClient.GetAsync("/api/policy/aggregated");
        agg.EnsureSuccessStatusCode();
    }
}

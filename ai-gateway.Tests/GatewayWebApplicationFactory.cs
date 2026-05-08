using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using OpenWorkmate.AI.Gateway.Services;

namespace OpenWorkmate.AI.Gateway.Tests;

public sealed class GatewayWebApplicationFactory : WebApplicationFactory<Program>
{
    /// <summary>隔离的 ContentRoot，内含可写的 appsettings.json（避免测试改动仓库内配置）。</summary>
    public string ContentRoot { get; } = Path.Combine(Path.GetTempPath(), "ai-gateway-tests-" + Guid.NewGuid().ToString("N"));

    public string DataRoot { get; } = Path.Combine(Path.GetTempPath(), "ai-gateway-tests-" + Guid.NewGuid().ToString("N"));

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        Directory.CreateDirectory(ContentRoot);
        Directory.CreateDirectory(DataRoot);
        File.WriteAllText(
            Path.Combine(DataRoot, TelemetryPolicyResolver.BundleFileName),
            "{\"schemaVersion\":1}\n");

        var appsettingsPath = Path.Combine(ContentRoot, "appsettings.json");
        File.WriteAllText(appsettingsPath, """
            {
              "Serilog": {
                "MinimumLevel": { "Default": "Information" },
                "WriteTo": [ { "Name": "Console" } ]
              },
              "AiGateway": {
                "ApiKey": "test-policy-key",
                "AdminApiKey": "",
                "DataRoot": "data",
                "RetentionDays": 1,
                "RetentionSweepHours": 168,
                "PolicyCacheSeconds": 1,
                "MaxEventPayloadChars": 50000
              },
              "Logging": {
                "LogLevel": {
                  "Default": "Information",
                  "Microsoft.AspNetCore": "Warning"
                }
              },
              "AllowedHosts": "*"
            }
            """);

        builder.UseContentRoot(ContentRoot);
        builder.UseEnvironment("Development");
        builder.ConfigureAppConfiguration((_, config) =>
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AiGateway:AdminApiKey"] = "test-admin-key",
                ["AiGateway:DataRoot"] = DataRoot,
                ["AiGateway:RetentionDays"] = "1",
                ["AiGateway:RetentionSweepHours"] = "168",
                ["AiGateway:PolicyCacheSeconds"] = "1"
            });
        });
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            try
            {
                if (Directory.Exists(DataRoot))
                    Directory.Delete(DataRoot, recursive: true);
                if (Directory.Exists(ContentRoot))
                    Directory.Delete(ContentRoot, recursive: true);
            }
            catch
            {
                /* ignore */
            }
        }

        base.Dispose(disposing);
    }
}

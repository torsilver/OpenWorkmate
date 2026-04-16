using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace Taskly.Telemetry.Relay.Tests;

public sealed class RelayWebApplicationFactory : WebApplicationFactory<Program>
{
    public string DataRoot { get; } = Path.Combine(Path.GetTempPath(), "telemetry-relay-tests-" + Guid.NewGuid().ToString("N"));

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        Directory.CreateDirectory(DataRoot);
        builder.UseEnvironment("Development");
        builder.ConfigureAppConfiguration((_, config) =>
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Telemetry:ApiKey"] = "test-ingest-key",
                ["Telemetry:AdminApiKey"] = "",
                ["Telemetry:DataRoot"] = DataRoot,
                ["Telemetry:RetentionDays"] = "1",
                ["Telemetry:RetentionSweepHours"] = "168",
                ["Telemetry:PolicyCacheSeconds"] = "1"
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
            }
            catch
            {
                /* ignore */
            }
        }

        base.Dispose(disposing);
    }
}

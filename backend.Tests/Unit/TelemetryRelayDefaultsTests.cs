using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using OfficeCopilot.Server;
using OfficeCopilot.Server.Services.Telemetry;
using Xunit;

namespace backend.Tests.Unit;

public sealed class TelemetryRelayDefaultsTests
{
    [Fact]
    public void LogOutboundRelayConfig_does_not_throw_when_seq_configured()
    {
        var app = new AppConfig
        {
            TelemetryEnabled = true,
            TelemetryRelayBaseUrl = TelemetryRelayDefaults.LocalDevBaseUrl,
            TelemetryRelayApiKey = "test-key"
        };
        var host = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Telemetry:SeqServerUrl"] = "http://127.0.0.1:5341"
            })
            .Build();
        TelemetryRelayDefaults.LogOutboundRelayConfig(NullLogger.Instance, app, host);
    }
}

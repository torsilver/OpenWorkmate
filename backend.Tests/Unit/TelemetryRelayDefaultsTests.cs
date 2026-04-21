using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using OfficeCopilot.Server;
using OfficeCopilot.Server.Services.Telemetry;
using Xunit;

namespace backend.Tests.Unit;

public sealed class TelemetryRelayDefaultsTests
{
    [Fact]
    public void LogOutboundRelayConfig_does_not_throw()
    {
        var app = new AppConfig
        {
            TelemetryEnabled = true,
            AiGatewayBaseUrl = TelemetryRelayDefaults.LocalDevBaseUrl,
            AiGatewayApiKey = "test-key"
        };
        var host = new ConfigurationBuilder().AddInMemoryCollection().Build();
        TelemetryRelayDefaults.LogOutboundRelayConfig(NullLogger.Instance, app, host);
    }
}

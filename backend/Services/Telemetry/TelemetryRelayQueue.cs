using System.Threading.Channels;

namespace OpenWorkmate.Server.Services.Telemetry;

public sealed class TelemetryRelayQueue : ITelemetryRelayQueue
{
    private readonly Channel<TelemetryRelayEvent> _channel = Channel.CreateBounded<TelemetryRelayEvent>(
        new BoundedChannelOptions(4000) { FullMode = BoundedChannelFullMode.DropWrite });

    public ChannelReader<TelemetryRelayEvent> Reader => _channel.Reader;

    public void TryEnqueue(TelemetryRelayEvent ev)
    {
        _channel.Writer.TryWrite(ev);
    }
}

using System.Diagnostics.Metrics;

namespace Industrial.Web.Api.Infrastructure;

public sealed class WebMetrics : IDisposable
{
    public const string MeterName = "Industrial.Web.Api";

    private readonly Meter _meter;
    private readonly Counter<long> _relayed;

    public WebMetrics()
    {
        _meter = new Meter(MeterName);

        _relayed = _meter.CreateCounter<long>(
            "web.messages.relayed",
            unit: "{message}",
            description: "Kafka messages pushed to connected dashboards."
        );

        RelayFailures = _meter.CreateCounter<long>(
            "web.relay.failures",
            unit: "{message}",
            description: "Messages the bridge could not relay."
        );
    }

    public Counter<long> RelayFailures { get; }

    public void RecordRelayed(string topic) =>
        _relayed.Add(1, new KeyValuePair<string, object?>("messaging.destination.name", topic));

    public void Dispose() => _meter.Dispose();
}

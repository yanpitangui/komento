using System.Diagnostics.Metrics;

namespace Komento.Sinks;

/// <summary>The dropped-event counter for one kind of sink, tagged by sink and reason.</summary>
internal sealed class SinkMetrics(string droppedCounterName, string description)
{
    private static readonly Meter Meter = new("Komento");

    public Counter<long> Dropped { get; } = Meter.CreateCounter<long>(droppedCounterName, description: description);

    public static SinkMetrics Exposures { get; } = new(
        "komento.exposures.sink.dropped",
        "Exposures dropped because a sink could not keep up, tagged by sink.");

    public static SinkMetrics Track { get; } = new(
        "komento.track.sink.dropped",
        "Conversion events dropped because a sink could not keep up, tagged by sink.");
}

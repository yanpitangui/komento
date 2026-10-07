using System.Diagnostics.Metrics;

namespace Komento.Sinks;

internal static class ExposureSinkMetrics
{
    private static readonly Meter Meter = new("Komento");

    public static readonly Counter<long> Dropped = Meter.CreateCounter<long>(
        "komento.exposures.sink.dropped",
        description: "Exposures dropped because a sink could not keep up, tagged by sink.");
}

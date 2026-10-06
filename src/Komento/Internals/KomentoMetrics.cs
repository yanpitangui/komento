using System.Diagnostics.Metrics;

namespace Komento.Internals;

internal static class KomentoMetrics
{
    private static readonly Meter Meter = new("Komento");

    public static readonly Counter<long> Exposures = Meter.CreateCounter<long>(
        "komento.exposures", description: "Experiment exposures, tagged by experiment and variant.");

    public static readonly Counter<long> ExposuresDropped = Meter.CreateCounter<long>(
        "komento.exposures.dropped", description: "Exposures dropped because the exposure stream was full.");
}

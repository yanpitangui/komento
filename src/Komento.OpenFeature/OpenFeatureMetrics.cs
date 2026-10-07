using System.Diagnostics.Metrics;

namespace Komento.OpenFeature;

internal static class OpenFeatureMetrics
{
    private static readonly Meter Meter = new("Komento");

    public static readonly Counter<long> TrackDropped = Meter.CreateCounter<long>(
        "komento.track.dropped", description: "Conversion events dropped, tagged by reason.");
}

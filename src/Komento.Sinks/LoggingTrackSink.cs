using Microsoft.Extensions.Logging;

namespace Komento;

/// <summary>Writes each conversion as a structured log entry. Useful to see what is flowing.</summary>
public sealed class LoggingTrackSink(ILoggerFactory loggerFactory) : ITrackSink
{
    private readonly ILogger _logger = loggerFactory.CreateLogger("Komento.Sinks.Track");

    public ValueTask WriteAsync(IReadOnlyList<TrackEvent> batch, CancellationToken ct)
    {
        foreach (var e in batch)
            _logger.LogInformation(
                "Conversion {EventName} subject {SubjectId} value {Value} at {Timestamp:o}",
                e.EventName, e.SubjectId, e.Value, e.Timestamp);
        return ValueTask.CompletedTask;
    }
}

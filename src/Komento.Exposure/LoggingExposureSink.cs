using Microsoft.Extensions.Logging;

namespace Komento;

/// <summary>Writes each exposure as a structured log entry. Useful to see what is flowing.</summary>
public sealed class LoggingExposureSink(ILoggerFactory loggerFactory) : IExposureSink
{
    private readonly ILogger _logger = loggerFactory.CreateLogger("Komento.Exposure.Sink");

    public ValueTask WriteAsync(IReadOnlyList<ExposureEvent> batch, CancellationToken ct)
    {
        foreach (var e in batch)
            _logger.LogInformation(
                "Exposure {FlagKey} subject {SubjectId} -> {Variant} (eligible: {IsEligible}, outsider: {IsOutsider}) at {Timestamp:o}",
                e.FlagKey, e.SubjectId, e.VariantName, e.IsEligible, e.IsOutsider, e.Timestamp);
        return ValueTask.CompletedTask;
    }
}

namespace Komento;

/// <summary>
/// Receives batches of exposures. Calls are sequential per sink (never overlapping), so implementations
/// need not be thread-safe. The token is cancelled when <see cref="ExposureSinkOptions.WriteTimeout"/>
/// elapses or the host stops. Throwing drops the batch; retry belongs inside the sink.
/// </summary>
public interface IExposureSink
{
    ValueTask WriteAsync(IReadOnlyList<ExposureEvent> batch, CancellationToken ct);
}

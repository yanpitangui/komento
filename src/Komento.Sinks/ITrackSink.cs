namespace Komento;

/// <summary>
/// Receives batches of conversion events. Calls are sequential per sink (never overlapping), so implementations
/// need not be thread-safe. The token is cancelled when <see cref="SinkOptions.WriteTimeout"/> elapses, or when
/// shutdown gives up on the sink after <see cref="SinkOptions.ShutdownFlushTimeout"/>.
/// Throwing drops the batch; retry belongs inside the sink.
/// </summary>
public interface ITrackSink
{
    ValueTask WriteAsync(IReadOnlyList<TrackEvent> batch, CancellationToken ct);
}

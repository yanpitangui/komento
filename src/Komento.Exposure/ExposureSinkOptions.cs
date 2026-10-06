namespace Komento;

public sealed class ExposureSinkOptions
{
    /// <summary>
    /// Identifies this sink in metrics (the <c>sink</c> tag) and logs. Defaults to <c>sink-{index}</c>.
    /// </summary>
    public string? Name { get; set; }

    /// <summary>Maximum number of exposures handed to the sink in one call.</summary>
    public int BatchSize { get; set; } = 100;

    /// <summary>A partial batch is flushed after this interval.</summary>
    public TimeSpan FlushInterval { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Batches buffered while the sink is busy. When full, new batches are dropped for this sink only
    /// and counted in <c>komento.exposures.sink.dropped</c>.
    /// </summary>
    public int MaxPendingBatches { get; set; } = 64;

    /// <summary>
    /// Maximum time for one write. The write's <see cref="CancellationToken"/> is cancelled after this
    /// and the batch is dropped.
    /// </summary>
    public TimeSpan WriteTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// On shutdown, how long to wait for pending batches to be written before giving up on this sink.
    /// </summary>
    public TimeSpan ShutdownFlushTimeout { get; set; } = TimeSpan.FromSeconds(10);
}

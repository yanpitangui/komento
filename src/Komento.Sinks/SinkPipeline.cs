using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using R3;

namespace Komento.Sinks;

/// <summary>
/// One sink's isolated pipeline: batching, a bounded queue of pending batches, and a sequential
/// consumer. A slow sink fills only its own queue; the producer side never blocks.
/// </summary>
internal sealed class SinkPipeline
{
    private readonly string                 _name;
    private readonly ILogger                _logger;
    private readonly TimeProvider           _timeProvider;
    private readonly Subject<ExposureEvent> _input = new();
    private readonly Channel<ExposureEvent[]> _pending;
    private readonly IDisposable            _batching;
    private readonly Task                   _consumer;
    private readonly CancellationTokenSource _cts = new();
    private readonly ExposureSinkOptions    _options;

    public SinkPipeline(
        string name, ExposureSinkRegistration registration, SinkWrite write, TimeProvider timeProvider, ILogger logger)
    {
        _name         = name;
        _logger       = logger;
        _timeProvider = timeProvider;
        var options = _options = registration.Options;

        _pending = Channel.CreateBounded<ExposureEvent[]>(new BoundedChannelOptions(options.MaxPendingBatches)
        {
            FullMode     = BoundedChannelFullMode.Wait,
            SingleReader = true
        });

        _batching = _input
            .Chunk(options.FlushInterval, options.BatchSize, timeProvider)
            .Subscribe(OnBatch);

        _consumer = Task.Run(() => ConsumeAsync(write, _cts.Token), CancellationToken.None);
    }

    public void Publish(ExposureEvent exposure) => _input.OnNext(exposure);

    private void OnBatch(ExposureEvent[] batch)
    {
        if (!_pending.Writer.TryWrite(batch))
            RecordDropped(batch.Length, "queue_full");
    }

    private void RecordDropped(int count, string reason)
        => ExposureSinkMetrics.Dropped.Add(count,
            new KeyValuePair<string, object?>("sink", _name),
            new KeyValuePair<string, object?>("reason", reason));

    private async Task ConsumeAsync(SinkWrite write, CancellationToken ct)
    {
        try
        {
            await foreach (var batch in _pending.Reader.ReadAllAsync(ct))
                await WriteAsync(write, batch, ct);
        }
        catch (OperationCanceledException) { }
    }

    private async Task WriteAsync(SinkWrite write, ExposureEvent[] batch, CancellationToken ct)
    {
        using var timeout = new CancellationTokenSource(_options.WriteTimeout, _timeProvider);
        using var linked  = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
        try
        {
            await write(batch, linked.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _logger.LogWarning("Exposure sink {Sink} timed out writing {Count} exposures; batch dropped.", _name, batch.Length);
            RecordDropped(batch.Length, "write_timeout");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Exposure sink {Sink} failed writing {Count} exposures; batch dropped.", _name, batch.Length);
            RecordDropped(batch.Length, "write_failed");
        }
    }

    /// <summary>
    /// Flushes the partial batch and waits up to <see cref="ExposureSinkOptions.ShutdownFlushTimeout"/>
    /// for pending batches to be written, then cancels whatever is still running.
    /// </summary>
    public async Task StopAsync()
    {
        _input.OnCompleted();          // Chunk emits its partial batch on completion
        _pending.Writer.TryComplete(); // consumer drains what is queued, then ends
        try
        {
            await _consumer.WaitAsync(_options.ShutdownFlushTimeout, _timeProvider);
        }
        catch (TimeoutException)
        {
            _logger.LogWarning("Exposure sink {Sink} did not flush within {Timeout}; abandoning pending exposures.",
                _name, _options.ShutdownFlushTimeout);
            _cts.Cancel();
        }
        finally
        {
            _batching.Dispose();
            _input.Dispose();
            _cts.Dispose();
        }
    }
}

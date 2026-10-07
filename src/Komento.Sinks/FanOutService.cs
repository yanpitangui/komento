using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Komento.Sinks;

/// <summary>Reads one event stream once and hands each event to every sink's own queue.</summary>
internal sealed class FanOutService<TEvent>(
    IServiceProvider                         services,
    Func<ChannelReader<TEvent>>              getReader,
    IEnumerable<SinkRegistration<TEvent>>    registrations,
    SinkMetrics                              metrics,
    string                                   kind,
    TimeProvider                             timeProvider,
    ILogger                                  logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Resolved here, not in the constructor, so a disabled stream fails the host start with
        // the stream's own message.
        var reader = getReader();

        var pipelines = registrations
            .Select((r, i) => new SinkPipeline<TEvent>(
                r.Options.Name ?? $"sink-{i}", r.Options, r.CreateWrite(services), metrics, kind, timeProvider, logger))
            .ToArray();
        try
        {
            await foreach (var evt in reader.ReadAllAsync(stoppingToken))
                foreach (var pipeline in pipelines)
                    pipeline.Publish(evt);
        }
        catch (OperationCanceledException) { }

        // Shutting down: hand over anything still in the stream, then flush every sink concurrently.
        while (reader.TryRead(out var remaining))
            foreach (var pipeline in pipelines)
                pipeline.Publish(remaining);

        await Task.WhenAll(pipelines.Select(p => p.StopAsync()));
    }
}

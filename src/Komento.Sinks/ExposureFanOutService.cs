using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Komento.Sinks;

/// <summary>Reads the exposure stream once and hands each exposure to every sink's own queue.</summary>
internal sealed class ExposureFanOutService(
    IServiceProvider                      services,
    IExposureStream                       stream,
    IEnumerable<ExposureSinkRegistration> registrations,
    TimeProvider                          timeProvider,
    ILogger                               logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var pipelines = registrations
            .Select((r, i) => new SinkPipeline(r.Options.Name ?? $"sink-{i}", r, r.CreateWrite(services), timeProvider, logger))
            .ToArray();
        try
        {
            await foreach (var exposure in stream.Reader.ReadAllAsync(stoppingToken))
                foreach (var pipeline in pipelines)
                    pipeline.Publish(exposure);
        }
        catch (OperationCanceledException) { }

        // Shutting down: hand over anything still in the stream, then flush every sink concurrently.
        while (stream.Reader.TryRead(out var remaining))
            foreach (var pipeline in pipelines)
                pipeline.Publish(remaining);

        await Task.WhenAll(pipelines.Select(p => p.StopAsync()));
    }
}

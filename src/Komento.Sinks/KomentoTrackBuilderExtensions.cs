using Komento.Sinks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Komento;

public static class KomentoTrackBuilderExtensions
{
    /// <summary>
    /// Registers a conversion sink implemented as <typeparamref name="TSink"/>, resolved from the container as a
    /// singleton. Each sink gets its own queue. Requires <see cref="KomentoOptions.EnableTrackStream"/>.
    /// </summary>
    public static KomentoBuilder AddTrackSink<TSink>(
        this KomentoBuilder builder, Action<TrackSinkOptions>? configure = null)
        where TSink : class, ITrackSink
    {
        builder.Services.TryAddSingleton<TSink>();
        return Register(builder, sp => sp.GetRequiredService<TSink>().WriteAsync, configure, typeof(TSink).Name);
    }

    /// <summary>Registers a conversion sink implemented as a delegate.</summary>
    public static KomentoBuilder AddTrackSink(
        this KomentoBuilder builder,
        Func<IReadOnlyList<TrackEvent>, CancellationToken, ValueTask> write,
        Action<TrackSinkOptions>? configure = null)
        => Register(builder, _ => new SinkWrite<TrackEvent>(write), configure, defaultName: null);

    /// <summary>Registers a sink that writes each conversion as a structured log entry.</summary>
    public static KomentoBuilder AddLoggingTrackSink(
        this KomentoBuilder builder, Action<TrackSinkOptions>? configure = null)
        => builder.AddTrackSink<LoggingTrackSink>(configure);

    private static KomentoBuilder Register(
        KomentoBuilder builder,
        Func<IServiceProvider, SinkWrite<TrackEvent>> createWrite,
        Action<TrackSinkOptions>? configure,
        string? defaultName)
    {
        var options = new TrackSinkOptions { Name = defaultName };
        configure?.Invoke(options);

        return SinkRegistrar.Register(
            builder,
            createWrite,
            options,
            sp => sp.GetRequiredService<ITrackStream>().Reader,
            SinkMetrics.Track,
            kind: "conversions");
    }
}

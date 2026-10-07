using Komento.Sinks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Komento;

public static class KomentoExposureBuilderExtensions
{
    /// <summary>
    /// Registers a sink implemented as <typeparamref name="TSink"/>, resolved from the container as a singleton.
    /// Each sink gets its own queue, so a slow sink never delays the others or evaluation.
    /// Requires <see cref="KomentoOptions.EnableExposureStream"/>.
    /// </summary>
    public static KomentoBuilder AddExposureSink<TSink>(
        this KomentoBuilder builder, Action<ExposureSinkOptions>? configure = null)
        where TSink : class, IExposureSink
    {
        builder.Services.TryAddSingleton<TSink>();
        return Register(builder, sp => sp.GetRequiredService<TSink>().WriteAsync, configure, typeof(TSink).Name);
    }

    /// <summary>Registers a sink implemented as a delegate.</summary>
    public static KomentoBuilder AddExposureSink(
        this KomentoBuilder builder,
        Func<IReadOnlyList<ExposureEvent>, CancellationToken, ValueTask> write,
        Action<ExposureSinkOptions>? configure = null)
        => Register(builder, _ => new SinkWrite<ExposureEvent>(write), configure, defaultName: null);

    /// <summary>Registers a sink that writes each exposure as a structured log entry.</summary>
    public static KomentoBuilder AddLoggingExposureSink(
        this KomentoBuilder builder, Action<ExposureSinkOptions>? configure = null)
        => builder.AddExposureSink<LoggingExposureSink>(configure);

    private static KomentoBuilder Register(
        KomentoBuilder builder,
        Func<IServiceProvider, SinkWrite<ExposureEvent>> createWrite,
        Action<ExposureSinkOptions>? configure,
        string? defaultName)
    {
        var options = new ExposureSinkOptions { Name = defaultName };
        configure?.Invoke(options);

        return SinkRegistrar.Register(
            builder,
            createWrite,
            options,
            sp => sp.GetRequiredService<IExposureStream>().Reader,
            SinkMetrics.Exposures,
            kind: "exposures");
    }
}

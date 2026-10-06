using Komento.Exposure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

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
        return builder.Register(
            sp =>
            {
                var sink = sp.GetRequiredService<TSink>();
                return sink.WriteAsync;
            },
            configure,
            defaultName: typeof(TSink).Name);
    }

    /// <summary>Registers a sink implemented as a delegate.</summary>
    public static KomentoBuilder AddExposureSink(
        this KomentoBuilder builder,
        Func<IReadOnlyList<ExposureEvent>, CancellationToken, ValueTask> write,
        Action<ExposureSinkOptions>? configure = null)
        => builder.Register(_ => new SinkWrite(write), configure, defaultName: null);

    /// <summary>Registers a sink that writes each exposure as a structured log entry.</summary>
    public static KomentoBuilder AddLoggingExposureSink(
        this KomentoBuilder builder, Action<ExposureSinkOptions>? configure = null)
        => builder.AddExposureSink<LoggingExposureSink>(configure);

    private static KomentoBuilder Register(
        this KomentoBuilder builder,
        Func<IServiceProvider, SinkWrite> createWrite,
        Action<ExposureSinkOptions>? configure,
        string? defaultName)
    {
        var options = new ExposureSinkOptions { Name = defaultName };
        configure?.Invoke(options);

        var services = builder.Services;
        services.AddSingleton(new ExposureSinkRegistration(createWrite, options));

        services.TryAddSingleton(sp => new ExposureFanOutService(
            sp,
            sp.GetRequiredService<IExposureStream>(),
            sp.GetServices<ExposureSinkRegistration>(),
            sp.GetService<TimeProvider>() ?? TimeProvider.System,
            sp.GetService<ILoggerFactory>()?.CreateLogger("Komento.Exposure") ?? NullLogger.Instance));
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, ExposureFanOutService>(
            sp => sp.GetRequiredService<ExposureFanOutService>()));

        return builder;
    }
}

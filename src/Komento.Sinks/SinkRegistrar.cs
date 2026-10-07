using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Komento.Sinks;

/// <summary>Registers one sink and (once per event type) the hosted service that feeds it.</summary>
internal static class SinkRegistrar
{
    public static KomentoBuilder Register<TEvent>(
        KomentoBuilder                                  builder,
        Func<IServiceProvider, SinkWrite<TEvent>>       createWrite,
        SinkOptions                                     options,
        Func<IServiceProvider, ChannelReader<TEvent>>   getReader,
        SinkMetrics                                     metrics,
        string                                          kind)
    {
        var services = builder.Services;
        services.AddSingleton(new SinkRegistration<TEvent>(createWrite, options));

        services.TryAddSingleton(sp => new FanOutService<TEvent>(
            sp,
            () => getReader(sp),
            sp.GetServices<SinkRegistration<TEvent>>(),
            metrics,
            kind,
            sp.GetService<TimeProvider>() ?? TimeProvider.System,
            sp.GetService<ILoggerFactory>()?.CreateLogger("Komento.Sinks") ?? NullLogger.Instance));
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, FanOutService<TEvent>>(
            sp => sp.GetRequiredService<FanOutService<TEvent>>()));

        return builder;
    }
}

using Komento.Internals;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Komento;

public sealed class KomentoBuilder
{
    public IServiceCollection Services { get; }

    internal KomentoBuilder(IServiceCollection services) => Services = services;

    public KomentoBuilder AddSource<TSource>() where TSource : class, IExperimentSource
    {
        Services.AddSingleton<IExperimentSource, TSource>();
        return this;
    }

    public KomentoBuilder AddSource(IExperimentSource instance)
    {
        Services.AddSingleton<IExperimentSource>(instance);
        return this;
    }

    public KomentoBuilder AddSegmentProvider<TProvider>() where TProvider : class, ISegmentProvider
    {
        Services.AddSingleton<ISegmentProvider, TProvider>();
        return this;
    }

    public KomentoBuilder AddPeriodicRefresh(TimeSpan interval, IReadOnlySet<string>? experimentIds = null)
    {
        var ids = experimentIds ?? new HashSet<string>();
        Services.AddSingleton<IHostedService>(sp =>
            ActivatorUtilities.CreateInstance<PeriodicRefreshService>(sp, ids, interval));
        return this;
    }
}

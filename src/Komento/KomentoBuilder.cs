using Microsoft.Extensions.DependencyInjection;

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
}

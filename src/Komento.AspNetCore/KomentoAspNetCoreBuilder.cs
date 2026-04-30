using Microsoft.Extensions.DependencyInjection;

namespace Komento.AspNetCore;

public sealed class KomentoAspNetCoreBuilder
{
    public IServiceCollection Services { get; }

    internal KomentoAspNetCoreBuilder(IServiceCollection services) => Services = services;

    public KomentoAspNetCoreBuilder AddSubjectProvider<T>() where T : class, ISubjectProvider
    {
        Services.AddSingleton<ISubjectProvider, T>();
        return this;
    }

    public KomentoAspNetCoreBuilder AddEnricher<T>() where T : class, IEvaluationContextEnricher
    {
        Services.AddSingleton<IEvaluationContextEnricher, T>();
        return this;
    }
}

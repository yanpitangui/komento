using Komento.Internals;
using Microsoft.Extensions.DependencyInjection;

namespace Komento;

public static class ServiceCollectionExtensions
{
    public static KomentoBuilder AddKomento(
        this IServiceCollection services,
        Action<KomentoOptions>? configure = null)
    {
        var options = new KomentoOptions();
        configure?.Invoke(options);

        services.AddSingleton(options);

        // Register the engine once as its concrete type (internal), then alias to both interfaces.
        // All three registrations resolve the same singleton instance.
        services.AddSingleton<ExperimentClient>(sp =>
            new ExperimentClient(
                sp.GetRequiredService<KomentoOptions>(),
                sp.GetService<ISegmentProvider>()));

        services.AddSingleton<IExperimentClient>(sp => sp.GetRequiredService<ExperimentClient>());
        services.AddSingleton<IConfigUpdater>(sp => sp.GetRequiredService<ExperimentClient>());

        return new KomentoBuilder(services);
    }
}

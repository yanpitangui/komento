using Microsoft.Extensions.DependencyInjection;

namespace Komento;

public static class ServiceProviderExtensions
{
    public static async ValueTask InitializeKomentoAsync(
        this IServiceProvider services, CancellationToken ct = default)
    {
        var source = services.GetService<IExperimentSource>();
        if (source is null) return;

        var updater = services.GetRequiredService<IConfigUpdater>();

        var configs = await source.LoadAsync(new HashSet<string>(), ct);
        await updater.UpdateAsync(configs, new HashSet<string>(), ct);
    }
}

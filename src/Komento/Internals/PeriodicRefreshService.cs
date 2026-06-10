using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Komento.Internals;

internal sealed class PeriodicRefreshService(
    IServiceProvider         services,
    IConfigUpdater           updater,
    IReadOnlySet<string>     experimentIds,
    TimeSpan                 interval) : BackgroundService
{
    private readonly TimeProvider _timeProvider =
        services.GetService<TimeProvider>() ?? TimeProvider.System;

    private readonly ILogger _logger =
        (services.GetService<ILoggerFactory>() ?? NullLoggerFactory.Instance)
            .CreateLogger<PeriodicRefreshService>();

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(interval, _timeProvider);
        while (await timer.WaitForNextTickAsync(ct))
        {
            try
            {
                var source = services.GetService<IExperimentSource>();
                if (source is null) return;

                var configs = await source.LoadAsync(experimentIds, ct);
                await updater.UpdateAsync(configs, experimentIds, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Komento periodic refresh failed; will retry next interval");
            }
        }
    }
}

using System.Text.Json;
using Komento;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NATS.Client.Core;
using NATS.Client.KeyValueStore;
using NATS.Net;

namespace Komento.Sample.EcommerceApi.Infrastructure;

internal sealed class NatsExperimentWatcher(
    INatsConnection nats,
    IConfigUpdater updater,
    ILogger<NatsExperimentWatcher> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var kv = nats.CreateKeyValueStoreContext();
        var store = await kv.CreateOrUpdateStoreAsync(new NatsKVConfig("experiments"), stoppingToken);

        await foreach (var entry in store.WatchAsync<string>(cancellationToken: stoppingToken))
        {
            try
            {
                if (entry.Operation == NatsKVOperation.Put && entry.Value is not null)
                {
                    var config = JsonSerializer.Deserialize<ExperimentConfig>(entry.Value, SampleJsonOptions.Default);
                    if (config is not null)
                    {
                        await updater.UpdateAsync(config, stoppingToken);
                        logger.LogInformation("Experiment {Id} updated live", config.Id);
                    }
                }
                else if (entry.Operation is NatsKVOperation.Del or NatsKVOperation.Purge)
                {
                    await updater.RemoveAsync(entry.Key, stoppingToken);
                    logger.LogInformation("Experiment {Id} removed", entry.Key);
                }
            }
            catch (JsonException ex)
            {
                logger.LogWarning(ex, "Failed to deserialize experiment config for key {Key}", entry.Key);
            }
        }
    }
}

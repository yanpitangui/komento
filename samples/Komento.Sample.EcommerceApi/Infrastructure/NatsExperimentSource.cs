using System.Text.Json;
using Komento;
using NATS.Client.Core;
using NATS.Client.KeyValueStore;
using NATS.Net;

namespace Komento.Sample.EcommerceApi.Infrastructure;

internal sealed class NatsExperimentSource(INatsConnection nats) : IExperimentSource
{
    public async ValueTask<IReadOnlyDictionary<string, ExperimentConfig>> LoadAsync(
        IReadOnlySet<string> experimentIds,
        CancellationToken ct = default)
    {
        var kv = nats.CreateKeyValueStoreContext();
        var store = await kv.CreateOrUpdateStoreAsync(new NatsKVConfig("experiments"), ct);
        var result = new Dictionary<string, ExperimentConfig>(StringComparer.Ordinal);

        foreach (var id in experimentIds)
        {
            try
            {
                var entry = await store.GetEntryAsync<string>(id, cancellationToken: ct);
                if (entry.Value is not null)
                    result[id] = JsonSerializer.Deserialize<ExperimentConfig>(entry.Value, SampleJsonOptions.Default)!;
            }
            catch (NatsKVKeyNotFoundException) { }
        }

        return result;
    }
}

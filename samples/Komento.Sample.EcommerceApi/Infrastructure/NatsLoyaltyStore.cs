using NATS.Client.Core;
using NATS.Client.KeyValueStore;
using NATS.Net;

namespace Komento.Sample.EcommerceApi.Infrastructure;

internal sealed class NatsLoyaltyStore(INatsConnection nats)
{
    private INatsKVStore? _store;

    private async ValueTask<INatsKVStore> GetStoreAsync(CancellationToken ct)
    {
        if (_store is not null) return _store;
        var kv = nats.CreateKeyValueStoreContext();
        _store = await kv.CreateOrUpdateStoreAsync(new NatsKVConfig("loyalty"), ct);
        return _store;
    }

    public async ValueTask<bool> IsMemberAsync(string subjectId, CancellationToken ct = default)
    {
        var store = await GetStoreAsync(ct);
        try
        {
            var entry = await store.GetEntryAsync<string>(subjectId, cancellationToken: ct);
            return entry.Value == "true";
        }
        catch (NatsKVKeyNotFoundException)
        {
            return false;
        }
    }
}

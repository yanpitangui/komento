using System.Text.Json;
using Komento;
using NATS.Client.Core;
using NATS.Client.KeyValueStore;
using NATS.Net;
using Npgsql;

namespace Komento.Sample.EcommerceApi.Infrastructure;

internal sealed class DataSeeder(INatsConnection nats, NpgsqlDataSource db)
{
    private static readonly string[] VipUsers =
        ["user-1", "user-2", "user-3", "user-4", "user-5",
         "user-6", "user-7", "user-8", "user-9", "user-10"];

    private static readonly string[] LoyaltyUsers = ["user-1", "user-2"];

    public async Task SeedAsync(CancellationToken ct = default)
    {
        await SeedPostgresAsync(ct);
        await SeedNatsAsync(ct);
    }

    private async Task SeedPostgresAsync(CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);

        await using var check = new NpgsqlCommand("SELECT COUNT(*) FROM vip_users", conn);
        var count = (long)(await check.ExecuteScalarAsync(ct))!;
        if (count > 0) return;

        foreach (var userId in VipUsers)
        {
            await using var insert = new NpgsqlCommand(
                "INSERT INTO vip_users (user_id) VALUES ($1) ON CONFLICT DO NOTHING", conn);
            insert.Parameters.AddWithValue(userId);
            await insert.ExecuteNonQueryAsync(ct);
        }
    }

    private async Task SeedNatsAsync(CancellationToken ct)
    {
        var kv = nats.CreateKeyValueStoreContext();

        var expStore = await kv.CreateOrUpdateStoreAsync(new NatsKVConfig("experiments"), ct);
        foreach (var (id, config) in ExperimentSeed.All)
        {
            try { await expStore.GetEntryAsync<string>(id, cancellationToken: ct); }
            catch (NatsKVKeyNotFoundException)
            {
                var json = JsonSerializer.Serialize(config, SampleJsonOptions.Default);
                await expStore.PutAsync(id, json, cancellationToken: ct);
            }
        }

        var loyaltyStore = await kv.CreateOrUpdateStoreAsync(new NatsKVConfig("loyalty"), ct);
        foreach (var userId in LoyaltyUsers)
        {
            try { await loyaltyStore.GetEntryAsync<string>(userId, cancellationToken: ct); }
            catch (NatsKVKeyNotFoundException)
            {
                await loyaltyStore.PutAsync(userId, "true", cancellationToken: ct);
            }
        }
    }
}

internal static class ExperimentSeed
{
    public static readonly IReadOnlyDictionary<string, ExperimentConfig> All =
        new Dictionary<string, ExperimentConfig>(StringComparer.Ordinal)
        {
            ["premium-product-page"] = new ExperimentConfig
            {
                Id          = "premium-product-page",
                SubjectType = "user",
                Variants    = [new VariantConfig { Name = "on", Allocation = 1.0, Value = true }],
                GlobalFilters = [new TraitEqualsFilter { Key = "plan", Value = "premium" }],
                Overrides   = []
            },
            ["price-display"] = new ExperimentConfig
            {
                Id          = "price-display",
                SubjectType = "user",
                Variants =
                [
                    new VariantConfig { Name = "default",       Allocation = 0.0, Value = "default"       },
                    new VariantConfig { Name = "vip-price",     Allocation = 1.0, Value = "vip-price"    },
                    new VariantConfig { Name = "loyalty-price", Allocation = 0.0, Value = "loyalty-price" }
                ],
                GlobalFilters = [new SegmentIncludeFilter { Segment = "vip" }],
                Overrides     = [new SegmentOverride { Segment = "loyalty", Variant = "loyalty-price" }]
            },
            ["recommendation-algorithm"] = new ExperimentConfig
            {
                Id          = "recommendation-algorithm",
                SubjectType = "user",
                Variants =
                [
                    new VariantConfig { Name = "collaborative", Allocation = 0.5, Value = "collaborative" },
                    new VariantConfig { Name = "content-based", Allocation = 0.5, Value = "content-based" }
                ],
                GlobalFilters = [],
                Overrides     = []
            }
        };
}

internal static class SampleJsonOptions
{
    public static readonly JsonSerializerOptions Default = new(JsonSerializerDefaults.Web);
}

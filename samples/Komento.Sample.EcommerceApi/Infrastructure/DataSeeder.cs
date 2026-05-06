using System.Text.Json;
using System.Text.Json.Serialization;
using Komento;
using NATS.Client.Core;
using NATS.Client.KeyValueStore;
using NATS.Net;

namespace Komento.Sample.EcommerceApi.Infrastructure;

internal sealed class DataSeeder(INatsConnection nats)
{
    private static readonly string[] LoyaltyUsers = ["user-1", "user-2"];

    public Task SeedAsync(CancellationToken ct = default) => SeedNatsAsync(ct);

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
    public static readonly JsonSerializerOptions Default = new(JsonSerializerDefaults.Web)
    {
        Converters = { new ObjectPrimitiveConverter() }
    };
}

// STJ deserializes object? as JsonElement by default; this maps JSON primitives to native types
// so that VariantConfig.Value round-trips correctly through NATS (bool stays bool, string stays string).
internal sealed class ObjectPrimitiveConverter : JsonConverter<object?>
{
    public override object? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => reader.TokenType switch
        {
            JsonTokenType.True   => true,
            JsonTokenType.False  => false,
            JsonTokenType.String => reader.GetString(),
            JsonTokenType.Number => reader.TryGetInt64(out var l) ? (object)l : reader.GetDouble(),
            JsonTokenType.Null   => null,
            _                    => JsonSerializer.Deserialize<JsonElement>(ref reader, options)
        };

    public override void Write(Utf8JsonWriter writer, object? value, JsonSerializerOptions options)
        => JsonSerializer.Serialize(writer, value, value?.GetType() ?? typeof(object), options);
}

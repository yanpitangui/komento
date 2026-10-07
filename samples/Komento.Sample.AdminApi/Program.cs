using System.Text.Json;
using Komento;
using Komento.Sample.Contracts;
using Komento.Sample.ServiceDefaults;
using Microsoft.AspNetCore.Http.HttpResults;
using NATS.Client.Core;
using NATS.Client.KeyValueStore;
using NATS.Net;
using Npgsql;

var builder = WebApplication.CreateBuilder(args);
builder.AddServiceDefaults();

builder.AddNatsClient("nats");
builder.AddNpgsqlDataSource("komento-db");

var app = builder.Build();

// Ensure KV buckets exist on startup
{
    var nats = app.Services.GetRequiredService<INatsConnection>();
    var kv   = nats.CreateKeyValueStoreContext();
    await kv.CreateOrUpdateStoreAsync(new NatsKVConfig("experiments"), app.Lifetime.ApplicationStopping);
    await kv.CreateOrUpdateStoreAsync(new NatsKVConfig("loyalty"),     app.Lifetime.ApplicationStopping);
}

app.MapDefaultEndpoints();

var jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);

// ── Experiment endpoints ───────────────────────────────────────────────────

app.MapGet("/experiments/{id}", async Task<Results<Ok<ExperimentConfig>, NotFound>> (
    string id, INatsConnection nats, CancellationToken ct) =>
{
    var kv    = nats.CreateKeyValueStoreContext();
    var store = await kv.CreateOrUpdateStoreAsync(new NatsKVConfig("experiments"), ct);
    try
    {
        var entry  = await store.GetEntryAsync<string>(id, cancellationToken: ct);
        var config = entry.Value is null
            ? null
            : JsonSerializer.Deserialize<ExperimentConfig>(entry.Value, jsonOptions);
        return config is null ? TypedResults.NotFound() : TypedResults.Ok(config);
    }
    catch (NatsKVKeyNotFoundException)
    {
        return TypedResults.NotFound();
    }
});

// The body is bound to ExperimentConfig, so a malformed or incomplete body is rejected with a 400
// before anything is stored.
app.MapPut("/experiments/{id}", async Task<Results<NoContent, ValidationProblem>> (
    string id, ExperimentConfig config, INatsConnection nats, CancellationToken ct) =>
{
    if (!string.Equals(config.Id, id, StringComparison.Ordinal))
        return TypedResults.ValidationProblem(new Dictionary<string, string[]>
        {
            [nameof(config.Id)] = [$"The body id '{config.Id}' does not match the route id '{id}'."]
        });

    var kv    = nats.CreateKeyValueStoreContext();
    var store = await kv.CreateOrUpdateStoreAsync(new NatsKVConfig("experiments"), ct);
    await store.PutAsync(id, JsonSerializer.Serialize(config, jsonOptions), cancellationToken: ct);
    return TypedResults.NoContent();
});

// ── Loyalty endpoints ──────────────────────────────────────────────────────

app.MapPut("/loyalty/{userId}", async (string userId, INatsConnection nats, CancellationToken ct) =>
{
    var kv    = nats.CreateKeyValueStoreContext();
    var store = await kv.CreateOrUpdateStoreAsync(new NatsKVConfig("loyalty"), ct);
    await store.PutAsync(userId, "true", cancellationToken: ct);
    return TypedResults.NoContent();
});

app.MapDelete("/loyalty/{userId}", async (string userId, INatsConnection nats, CancellationToken ct) =>
{
    var kv    = nats.CreateKeyValueStoreContext();
    var store = await kv.CreateOrUpdateStoreAsync(new NatsKVConfig("loyalty"), ct);
    await store.DeleteAsync(userId, cancellationToken: ct);
    return TypedResults.NoContent();
});

// ── VIP endpoints ─────────────────────────────────────────────────────────

app.MapGet("/vip", async (NpgsqlDataSource db, CancellationToken ct) =>
{
    await using var conn   = await db.OpenConnectionAsync(ct);
    await using var cmd    = new NpgsqlCommand("SELECT user_id FROM vip_users ORDER BY user_id", conn);
    await using var reader = await cmd.ExecuteReaderAsync(ct);

    var ids = new List<string>();
    while (await reader.ReadAsync(ct))
        ids.Add(reader.GetString(0));

    return TypedResults.Ok(ids);
});

app.MapPost("/vip/{userId}", async (string userId, NpgsqlDataSource db, CancellationToken ct) =>
{
    await using var conn = await db.OpenConnectionAsync(ct);
    await using var cmd  = new NpgsqlCommand(
        "INSERT INTO vip_users (user_id) VALUES ($1) ON CONFLICT DO NOTHING", conn);
    cmd.Parameters.AddWithValue(userId);
    await cmd.ExecuteNonQueryAsync(ct);
    return TypedResults.NoContent();
});

// ── Exposure endpoints ────────────────────────────────────────────────────

app.MapGet("/exposures", async Task<Results<Ok<IReadOnlyList<ExposureResponse>>, ValidationProblem>> (
    string subjectId, NpgsqlDataSource db, CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(subjectId))
        return TypedResults.ValidationProblem(new Dictionary<string, string[]>
        {
            [nameof(subjectId)] = ["subjectId is required."]
        });

    await using var conn = await db.OpenConnectionAsync(ct);
    await using var cmd  = new NpgsqlCommand(
        "SELECT experiment, subject_id, subject_type, variant, is_eligible, is_outsider, exposed_at " +
        "FROM exposures WHERE subject_id = $1 ORDER BY exposed_at DESC, id DESC LIMIT 100", conn);
    cmd.Parameters.AddWithValue(subjectId);
    await using var reader = await cmd.ExecuteReaderAsync(ct);

    var exposures = new List<ExposureResponse>();
    while (await reader.ReadAsync(ct))
        exposures.Add(new ExposureResponse(
            Experiment:  reader.GetString(0),
            SubjectId:   reader.GetString(1),
            SubjectType: reader.GetString(2),
            Variant:     reader.GetString(3),
            IsEligible:  reader.GetBoolean(4),
            IsOutsider:  reader.GetBoolean(5),
            ExposedAt:   reader.GetFieldValue<DateTimeOffset>(6)));

    return TypedResults.Ok<IReadOnlyList<ExposureResponse>>(exposures);
});

// ── Conversion endpoints ──────────────────────────────────────────────────

app.MapGet("/conversions", async Task<Results<Ok<IReadOnlyList<ConversionResponse>>, ValidationProblem>> (
    string subjectId, NpgsqlDataSource db, CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(subjectId))
        return TypedResults.ValidationProblem(new Dictionary<string, string[]>
        {
            [nameof(subjectId)] = ["subjectId is required."]
        });

    await using var conn = await db.OpenConnectionAsync(ct);
    await using var cmd  = new NpgsqlCommand(
        "SELECT event_name, subject_id, value, properties::text, context::text, recorded_at " +
        "FROM conversions WHERE subject_id = $1 ORDER BY recorded_at DESC, id DESC LIMIT 100", conn);
    cmd.Parameters.AddWithValue(subjectId);
    await using var reader = await cmd.ExecuteReaderAsync(ct);

    var conversions = new List<ConversionResponse>();
    while (await reader.ReadAsync(ct))
        conversions.Add(new ConversionResponse(
            EventName:  reader.GetString(0),
            SubjectId:  reader.GetString(1),
            Value:      reader.IsDBNull(2) ? null : reader.GetDouble(2),
            Properties: ReadFields(reader, 3),
            Context:    ReadFields(reader, 4),
            RecordedAt: reader.GetFieldValue<DateTimeOffset>(5)));

    return TypedResults.Ok<IReadOnlyList<ConversionResponse>>(conversions);

    static Dictionary<string, string?>? ReadFields(NpgsqlDataReader reader, int ordinal)
        => reader.IsDBNull(ordinal)
            ? null
            : JsonSerializer.Deserialize<Dictionary<string, string?>>(reader.GetString(ordinal));
});

app.Run();

using System.Text.Json;
using Komento;
using Komento.Sample.ServiceDefaults;
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

app.MapGet("/experiments/{id}", async (string id, INatsConnection nats, CancellationToken ct) =>
{
    var kv    = nats.CreateKeyValueStoreContext();
    var store = await kv.CreateOrUpdateStoreAsync(new NatsKVConfig("experiments"), ct);
    try
    {
        var entry = await store.GetEntryAsync<string>(id, cancellationToken: ct);
        return Results.Text(entry.Value ?? "", "application/json");
    }
    catch (NatsKVKeyNotFoundException)
    {
        return Results.NotFound();
    }
});

app.MapPut("/experiments/{id}", async (string id, HttpRequest request, INatsConnection nats, CancellationToken ct) =>
{
    using var reader = new StreamReader(request.Body);
    var body = await reader.ReadToEndAsync(ct);

    // Validate it's a parseable ExperimentConfig before storing
    JsonSerializer.Deserialize<ExperimentConfig>(body, jsonOptions);

    var kv    = nats.CreateKeyValueStoreContext();
    var store = await kv.CreateOrUpdateStoreAsync(new NatsKVConfig("experiments"), ct);
    await store.PutAsync(id, body, cancellationToken: ct);
    return Results.NoContent();
});

// ── Loyalty endpoints ──────────────────────────────────────────────────────

app.MapPut("/loyalty/{userId}", async (string userId, INatsConnection nats, CancellationToken ct) =>
{
    var kv    = nats.CreateKeyValueStoreContext();
    var store = await kv.CreateOrUpdateStoreAsync(new NatsKVConfig("loyalty"), ct);
    await store.PutAsync(userId, "true", cancellationToken: ct);
    return Results.NoContent();
});

app.MapDelete("/loyalty/{userId}", async (string userId, INatsConnection nats, CancellationToken ct) =>
{
    var kv    = nats.CreateKeyValueStoreContext();
    var store = await kv.CreateOrUpdateStoreAsync(new NatsKVConfig("loyalty"), ct);
    await store.DeleteAsync(userId, cancellationToken: ct);
    return Results.NoContent();
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

    return Results.Ok(ids);
});

app.MapPost("/vip/{userId}", async (string userId, NpgsqlDataSource db, CancellationToken ct) =>
{
    await using var conn = await db.OpenConnectionAsync(ct);
    await using var cmd  = new NpgsqlCommand(
        "INSERT INTO vip_users (user_id) VALUES ($1) ON CONFLICT DO NOTHING", conn);
    cmd.Parameters.AddWithValue(userId);
    await cmd.ExecuteNonQueryAsync(ct);
    return Results.Created($"/vip/{userId}", null);
});

app.Run();

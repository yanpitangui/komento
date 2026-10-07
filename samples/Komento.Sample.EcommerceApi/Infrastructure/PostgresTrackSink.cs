using System.Text.Json;
using Komento;
using Npgsql;
using NpgsqlTypes;

namespace Komento.Sample.EcommerceApi.Infrastructure;

/// <summary>Writes each batch of conversions to PostgreSQL with a single binary COPY.</summary>
internal sealed class PostgresTrackSink(NpgsqlDataSource db) : ITrackSink
{
    private const string Copy =
        "COPY conversions (event_name, subject_id, value, properties, context, recorded_at) " +
        "FROM STDIN (FORMAT BINARY)";

    public async ValueTask WriteAsync(IReadOnlyList<TrackEvent> batch, CancellationToken ct)
    {
        await using var conn   = await db.OpenConnectionAsync(ct);
        await using var writer = await conn.BeginBinaryImportAsync(Copy, ct);

        foreach (var e in batch)
        {
            await writer.StartRowAsync(ct);
            await writer.WriteAsync(e.EventName, NpgsqlDbType.Text, ct);
            await writer.WriteAsync(e.SubjectId, NpgsqlDbType.Text, ct);

            if (e.Value is { } value) await writer.WriteAsync(value, NpgsqlDbType.Double, ct);
            else                      await writer.WriteNullAsync(ct);

            await WriteJsonAsync(writer, e.Properties?.ToDictionary(p => p.Key, p => p.Value?.ToString()), ct);
            await WriteJsonAsync(writer, e.Context?.ToDictionary(p => p.Key, p => (string?)p.Value.ToString()), ct);

            await writer.WriteAsync(e.Timestamp, NpgsqlDbType.TimestampTz, ct);
        }

        await writer.CompleteAsync(ct);
    }

    private static async ValueTask WriteJsonAsync(
        NpgsqlBinaryImporter writer, Dictionary<string, string?>? fields, CancellationToken ct)
    {
        if (fields is null) await writer.WriteNullAsync(ct);
        else                await writer.WriteAsync(JsonSerializer.Serialize(fields), NpgsqlDbType.Jsonb, ct);
    }
}

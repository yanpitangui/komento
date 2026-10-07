using System.Text.Json;
using Komento;
using Npgsql;
using NpgsqlTypes;

namespace Komento.Sample.EcommerceApi.Infrastructure;

/// <summary>Writes each batch of exposures to PostgreSQL with a single binary COPY.</summary>
internal sealed class PostgresExposureSink(NpgsqlDataSource db) : IExposureSink
{
    private const string Copy =
        "COPY exposures (experiment, subject_id, subject_type, variant, is_eligible, is_outsider, exposed_at, config_revision, context) " +
        "FROM STDIN (FORMAT BINARY)";

    public async ValueTask WriteAsync(IReadOnlyList<ExposureEvent> batch, CancellationToken ct)
    {
        await using var conn   = await db.OpenConnectionAsync(ct);
        await using var writer = await conn.BeginBinaryImportAsync(Copy, ct);

        foreach (var e in batch)
        {
            await writer.StartRowAsync(ct);
            await writer.WriteAsync(e.FlagKey,     NpgsqlDbType.Text,        ct);
            await writer.WriteAsync(e.SubjectId,   NpgsqlDbType.Text,        ct);
            await writer.WriteAsync(e.SubjectType, NpgsqlDbType.Text,        ct);
            await writer.WriteAsync(e.VariantName, NpgsqlDbType.Text,        ct);
            await writer.WriteAsync(e.IsEligible,  NpgsqlDbType.Boolean,     ct);
            await writer.WriteAsync(e.IsOutsider,  NpgsqlDbType.Boolean,     ct);
            await writer.WriteAsync(e.Timestamp,   NpgsqlDbType.TimestampTz, ct);

            if (e.ConfigRevision is { } revision) await writer.WriteAsync(revision, NpgsqlDbType.Text, ct);
            else                                  await writer.WriteNullAsync(ct);

            if (e.Context is { } context)
                await writer.WriteAsync(
                    JsonSerializer.Serialize(context.ToDictionary(p => p.Key, p => p.Value.ToString())),
                    NpgsqlDbType.Jsonb, ct);
            else
                await writer.WriteNullAsync(ct);
        }

        await writer.CompleteAsync(ct);
    }
}

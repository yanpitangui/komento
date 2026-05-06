using Komento;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Komento.Sample.EcommerceApi.Infrastructure;

internal sealed class VipBinSetStore(NpgsqlDataSource db, ILogger<VipBinSetStore> logger) : IHostedService, IDisposable
{
    private sealed class Snapshot(ReadOnlyMemory<byte> data)
    {
        public ReadOnlyMemory<byte> Data { get; } = data;
    }

    private Snapshot _snapshot = new(ReadOnlyMemory<byte>.Empty);
    private Timer?   _timer;

    public bool Contains(string subjectId)
    {
        var snap = Volatile.Read(ref _snapshot);
        return BinSet.Contains(snap.Data, subjectId);
    }

    public async Task StartAsync(CancellationToken ct)
    {
        await RefreshAsync(ct);
        _timer = new Timer(_ => _ = RefreshAsync(CancellationToken.None), null,
            TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5));
    }

    public Task StopAsync(CancellationToken ct)
    {
        _timer?.Change(Timeout.Infinite, 0);
        return Task.CompletedTask;
    }

    public void Dispose() => _timer?.Dispose();

    private async Task RefreshAsync(CancellationToken ct)
    {
        try
        {
            await using var conn   = await db.OpenConnectionAsync(ct);
            await using var cmd    = new NpgsqlCommand("SELECT user_id FROM vip_users", conn);
            await using var reader = await cmd.ExecuteReaderAsync(ct);

            var ids = new List<string>();
            while (await reader.ReadAsync(ct))
                ids.Add(reader.GetString(0));

            var newBinSet = BinSet.Build(ids);
            Volatile.Write(ref _snapshot, new Snapshot(newBinSet));
            logger.LogInformation("VIP BinSet refreshed: {Count} entries", ids.Count);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogError(ex, "Failed to refresh VIP BinSet");
        }
    }
}

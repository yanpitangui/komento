namespace Komento;

public interface IConfigUpdater
{
    ValueTask UpdateAsync(IReadOnlyDictionary<string, ExperimentConfig> configs, IReadOnlySet<string> experimentIds, CancellationToken ct = default);
    ValueTask UpdateAsync(IReadOnlyDictionary<string, ExperimentConfig> configs, CancellationToken ct = default);
    ValueTask UpdateAsync(ExperimentConfig config, CancellationToken ct = default);
    ValueTask RemoveAsync(string experimentId, CancellationToken ct = default);
}

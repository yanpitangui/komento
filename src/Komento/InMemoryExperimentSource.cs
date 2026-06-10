namespace Komento;

public sealed class InMemoryExperimentSource : IExperimentSource
{
    private readonly Dictionary<string, ExperimentConfig> _configs = new(StringComparer.Ordinal);

    public InMemoryExperimentSource Set(ExperimentConfig config)
    {
        _configs[config.Id] = config;
        return this;
    }

    public InMemoryExperimentSource Remove(string experimentId)
    {
        _configs.Remove(experimentId);
        return this;
    }

    public ValueTask<IReadOnlyDictionary<string, ExperimentConfig>> LoadAsync(
        IReadOnlySet<string> experimentIds,
        CancellationToken ct = default)
    {
        var loadAll = experimentIds.Count == 0;
        IReadOnlyDictionary<string, ExperimentConfig> result = loadAll
            ? new Dictionary<string, ExperimentConfig>(_configs, StringComparer.Ordinal)
            : _configs
                .Where(kv => experimentIds.Contains(kv.Key))
                .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);

        return ValueTask.FromResult(result);
    }
}

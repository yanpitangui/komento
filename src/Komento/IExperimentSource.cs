namespace Komento;

public interface IExperimentSource
{
    ValueTask<IReadOnlyDictionary<string, ExperimentConfig>> LoadAsync(
        IReadOnlySet<string> experimentIds,
        CancellationToken ct = default);
}

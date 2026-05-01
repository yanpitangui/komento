namespace Komento;

public interface IExperimentClient
{
    ValueTask<VariantResult> GetVariantAsync(string flagKey, string subjectId, in EvaluationContext ctx, CancellationToken ct = default);

    ValueTask<bool>   GetBoolAsync  (string flagKey, string subjectId, in EvaluationContext ctx, bool   defaultValue = default, CancellationToken ct = default);
    ValueTask<string> GetStringAsync(string flagKey, string subjectId, in EvaluationContext ctx, string defaultValue = "",      CancellationToken ct = default);
    ValueTask<int>    GetIntAsync   (string flagKey, string subjectId, in EvaluationContext ctx, int    defaultValue = default, CancellationToken ct = default);
    ValueTask<double> GetDoubleAsync(string flagKey, string subjectId, in EvaluationContext ctx, double defaultValue = default, CancellationToken ct = default);

    bool ExperimentExists(string flagKey);
}

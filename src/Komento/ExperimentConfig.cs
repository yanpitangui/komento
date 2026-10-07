namespace Komento;

public sealed class ExperimentConfig
{
    public required string                       Id            { get; init; }
    public required string                       SubjectType   { get; init; }
    public required IReadOnlyList<VariantConfig> Variants      { get; init; }
    public          IReadOnlyList<FilterConfig>  GlobalFilters { get; init; } = [];
    public          IReadOnlyList<OverrideRule>  Overrides     { get; init; } = [];

    /// <summary>
    /// Evaluation-context attributes to record on this experiment's exposures, by key. Keys not present in the
    /// context of an evaluation are omitted. Empty (the default) records no context.
    /// </summary>
    public          IReadOnlyList<string>        ExposureContext { get; init; } = [];
}

namespace Komento;

public sealed class ExperimentConfig
{
    public required string                       Id            { get; init; }
    public required string                       SubjectType   { get; init; }
    public required IReadOnlyList<VariantConfig> Variants      { get; init; }
    public          IReadOnlyList<FilterConfig>  GlobalFilters { get; init; } = [];
    public          IReadOnlyList<OverrideRule>  Overrides     { get; init; } = [];
}

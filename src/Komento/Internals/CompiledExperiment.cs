namespace Komento.Internals;

internal sealed class CompiledExperiment
{
    public string            Id          { get; init; } = "";
    public string            SubjectType { get; init; } = "";
    public CompiledVariant[] Variants    { get; init; } = [];
    public FilterConfig[]    Filters     { get; init; } = [];
    public OverrideRule[]    Overrides   { get; init; } = [];
}

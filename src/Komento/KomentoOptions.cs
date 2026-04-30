namespace Komento;

public sealed class KomentoOptions
{
    public IReadOnlySet<string>            Experiments   { get; init; } = new HashSet<string>();
    public EvaluationContext               StaticContext  { get; init; } = EvaluationContext.Empty;
    public Func<ExposureEvent, ValueTask>? OnExposure    { get; init; }
}

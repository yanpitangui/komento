namespace Komento;

public sealed class KomentoOptions
{
    public IReadOnlySet<string> Experiments            { get; set; } = new HashSet<string>();
    public EvaluationContext    StaticContext           { get; set; } = EvaluationContext.Empty;
    public int                  ExposureChannelCapacity { get; set; } = 4096;
}

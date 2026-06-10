namespace Komento;

public sealed class KomentoOptions
{
    public EvaluationContext StaticContext           { get; set; } = EvaluationContext.Empty;
    public int                  ExposureChannelCapacity { get; set; } = 4096;
}

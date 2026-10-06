namespace Komento;

public sealed class KomentoOptions
{
    public EvaluationContext StaticContext           { get; set; } = EvaluationContext.Empty;
    public int                  ExposureChannelCapacity { get; set; } = 4096;

    /// <summary>
    /// When true, exposures are written to a bounded channel readable via <see cref="IExposureStream"/>.
    /// Off by default: with no consumer the channel would only fill up and drop.
    /// </summary>
    public bool                 EnableExposureStream    { get; set; }
}

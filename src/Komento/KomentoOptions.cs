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

    /// <summary>
    /// Record each exposure as a <c>feature_flag.evaluation</c> event on <see cref="System.Diagnostics.Activity.Current"/>
    /// (for example the ASP.NET Core request span). Does nothing when no trace is being recorded.
    /// </summary>
    public bool                 EmitActivityEvents                { get; set; } = true;

    /// <summary>
    /// Also record the subject ID (<c>feature_flag.context.id</c>) on activity events. Off by default
    /// because subject IDs in traces may be personal data.
    /// </summary>
    public bool                 IncludeSubjectIdInActivityEvents  { get; set; }
}

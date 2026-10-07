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
    /// When true, conversions recorded through <see cref="IExperimentTracker"/> are written to a bounded channel
    /// readable via <see cref="ITrackStream"/>. Off by default: <c>Track</c> does nothing until it is enabled.
    /// </summary>
    public bool                 EnableTrackStream       { get; set; }

    /// <summary>Capacity of the track channel. When it is full, new conversion events are dropped and counted.</summary>
    public int                  TrackChannelCapacity    { get; set; } = 4096;

    /// <summary>
    /// Record the evaluation context (the static context plus the per-call context) on each conversion event.
    /// Set to false if context attributes are personal data that must not be stored with conversions.
    /// </summary>
    public bool                 IncludeContextInTrackEvents { get; set; } = true;

    /// <summary>
    /// Record each exposure as a <c>feature_flag.evaluation</c> event on <see cref="System.Diagnostics.Activity.Current"/>
    /// (for example the ASP.NET Core request span). Does nothing when no trace is being recorded.
    /// </summary>
    public bool                 EmitActivityEvents                { get; set; } = true;

    /// <summary>
    /// Record the subject ID (<c>feature_flag.context.id</c>) on activity events, so an exposure can be tied to
    /// the subject in the trace. Set to false if subject IDs are personal data that must stay out of your traces.
    /// </summary>
    public bool                 IncludeSubjectIdInActivityEvents  { get; set; } = true;
}

namespace Komento;

/// <summary>
/// A conversion: something a subject did that an experiment may be measured on (a purchase, a signup).
/// </summary>
public readonly struct TrackEvent
{
    public string?                               EventName  { get; init; }
    public string?                               SubjectId  { get; init; }

    /// <summary>An optional numeric value, such as an amount or a duration.</summary>
    public double?                               Value      { get; init; }

    /// <summary>Free-form fields supplied with the call (a snapshot taken when it was made).</summary>
    public IReadOnlyDictionary<string, object?>? Properties { get; init; }

    /// <summary>
    /// The evaluation context at the time of the call: <see cref="KomentoOptions.StaticContext"/> plus the
    /// per-call context, with per-call attributes taking precedence. Null when empty or when
    /// <see cref="KomentoOptions.IncludeContextInTrackEvents"/> is off.
    /// </summary>
    public IReadOnlyDictionary<string, object>?  Context    { get; init; }

    public DateTimeOffset                        Timestamp  { get; init; }
}

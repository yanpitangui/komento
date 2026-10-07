namespace Komento;

/// <summary>
/// Records conversions. Calls never block: the event is queued for <see cref="ITrackStream"/> consumers.
/// When <see cref="KomentoOptions.EnableTrackStream"/> is off, calls do nothing.
/// </summary>
public interface IExperimentTracker
{
    /// <exception cref="ArgumentException"><paramref name="eventName"/> or <paramref name="subjectId"/> is null or empty.</exception>
    void Track(string eventName, string subjectId, in EvaluationContext ctx,
               double? value = null, IReadOnlyDictionary<string, object?>? properties = null);

    /// <exception cref="ArgumentException"><paramref name="eventName"/> or <paramref name="subjectId"/> is null or empty.</exception>
    void Track(string eventName, string subjectId,
               double? value = null, IReadOnlyDictionary<string, object?>? properties = null);
}

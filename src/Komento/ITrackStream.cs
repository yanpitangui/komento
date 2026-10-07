using System.Threading.Channels;

namespace Komento;

/// <summary>
/// Stream of conversion events recorded through <see cref="IExperimentTracker"/>. Requires
/// <see cref="KomentoOptions.EnableTrackStream"/>.
/// </summary>
public interface ITrackStream
{
    /// <exception cref="InvalidOperationException">The track stream is not enabled.</exception>
    ChannelReader<TrackEvent> Reader { get; }
}

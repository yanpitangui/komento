using System.Threading.Channels;

namespace Komento;

/// <summary>
/// Stream of exposure events, one per variant evaluation. Requires
/// <see cref="KomentoOptions.EnableExposureStream"/>.
/// </summary>
public interface IExposureStream
{
    /// <exception cref="InvalidOperationException">The exposure stream is not enabled.</exception>
    ChannelReader<ExposureEvent> Reader { get; }
}

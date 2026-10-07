namespace Komento;

/// <summary>Announces when experiment configs change in the engine.</summary>
public interface IConfigChanges
{
    /// <summary>
    /// Raised after configs were applied, with the ids of the experiments that were added, removed or
    /// changed. Not raised when an update changed nothing, so a poll that finds the same configs is silent.
    /// Subscribers should not throw: an exception from one subscriber is swallowed and does not affect the
    /// update or other subscribers.
    /// </summary>
    event Action<IReadOnlyCollection<string>> Changed;
}

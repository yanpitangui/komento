namespace Komento.Sinks;

internal delegate ValueTask SinkWrite<TEvent>(IReadOnlyList<TEvent> batch, CancellationToken ct);

internal sealed record SinkRegistration<TEvent>(
    Func<IServiceProvider, SinkWrite<TEvent>> CreateWrite,
    SinkOptions Options);

namespace Komento.Exposure;

internal delegate ValueTask SinkWrite(IReadOnlyList<ExposureEvent> batch, CancellationToken ct);

internal sealed record ExposureSinkRegistration(
    Func<IServiceProvider, SinkWrite> CreateWrite,
    ExposureSinkOptions Options);

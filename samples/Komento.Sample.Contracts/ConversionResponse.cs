namespace Komento.Sample.Contracts;

/// <summary>A conversion recorded in PostgreSQL by the sample's track sink.</summary>
public sealed record ConversionResponse(
    string                                EventName,
    string                                SubjectId,
    double?                               Value,
    IReadOnlyDictionary<string, string?>? Properties,
    IReadOnlyDictionary<string, string?>? Context,
    DateTimeOffset                        RecordedAt);

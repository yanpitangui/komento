namespace Komento.Sample.Contracts;

/// <summary>An exposure recorded in PostgreSQL by the sample's exposure sink.</summary>
public sealed record ExposureResponse(
    string         Experiment,
    string         SubjectId,
    string         SubjectType,
    string         Variant,
    bool           IsEligible,
    bool           IsOutsider,
    DateTimeOffset ExposedAt);

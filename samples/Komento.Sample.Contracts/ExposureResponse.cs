namespace Komento.Sample.Contracts;

/// <summary>An exposure recorded in PostgreSQL by the sample's exposure sink.</summary>
/// <param name="ConfigRevision">Fingerprint of the experiment's definition when the exposure was recorded.</param>
/// <param name="Context">The attributes the experiment lists under <c>exposureContext</c>, if any were present.</param>
public sealed record ExposureResponse(
    string         Experiment,
    string         SubjectId,
    string         SubjectType,
    string         Variant,
    bool           IsEligible,
    bool           IsOutsider,
    DateTimeOffset ExposedAt,
    string?        ConfigRevision,
    IReadOnlyDictionary<string, string?>? Context);

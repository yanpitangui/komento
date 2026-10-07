namespace Komento;

public readonly struct ExposureEvent
{
    public string?        FlagKey     { get; init; }
    public string?        SubjectId   { get; init; }
    public string?        SubjectType { get; init; }
    public string?        VariantName { get; init; }
    public bool           IsEligible  { get; init; }
    public bool           IsOutsider  { get; init; }
    public DateTimeOffset Timestamp   { get; init; }

    /// <summary>
    /// Fingerprint of the experiment's definition (variants, allocations, filters, overrides) when this exposure
    /// was recorded. It changes when the definition changes. It does not change when segment membership
    /// changes, because membership lives in the <see cref="ISegmentProvider"/>, not in the definition.
    /// </summary>
    public string?        ConfigRevision { get; init; }
}

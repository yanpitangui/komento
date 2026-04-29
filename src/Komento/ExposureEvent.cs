namespace Komento;

public readonly struct ExposureEvent
{
    public string?        FlagKey     { get; init; }
    public string?        SubjectId   { get; init; }
    public string?        VariantName { get; init; }
    public bool           IsEligible  { get; init; }
    public bool           IsOutsider  { get; init; }
    public DateTimeOffset Timestamp   { get; init; }
}

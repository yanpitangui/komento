using System.Text.Json.Serialization;

namespace Komento;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(SubjectOverride), "subject")]
[JsonDerivedType(typeof(SegmentOverride), "segment")]
public abstract class OverrideRule { }

public sealed class SubjectOverride : OverrideRule
{
    public required string SubjectId { get; init; }
    public required string Variant   { get; init; }
}

public sealed class SegmentOverride : OverrideRule
{
    public required string Segment { get; init; }
    public required string Variant { get; init; }
}

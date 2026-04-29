using System.Text.Json.Serialization;

namespace Komento;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(TraitEqualsFilter),    "trait-equals")]
[JsonDerivedType(typeof(SegmentIncludeFilter), "segment-include")]
public abstract class FilterConfig { }

public sealed class TraitEqualsFilter : FilterConfig
{
    public required string Key   { get; init; }
    public required string Value { get; init; }
}

public sealed class SegmentIncludeFilter : FilterConfig
{
    public required string Segment { get; init; }
}

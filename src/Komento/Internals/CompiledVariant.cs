namespace Komento.Internals;

internal readonly struct CompiledVariant
{
    public string        Name   { get; init; }
    public object?       Value  { get; init; }
    public BucketRange[] Ranges { get; init; }
}

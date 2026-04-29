namespace Komento;

public sealed class VariantConfig
{
    public required string  Name       { get; init; }
    public required double  Allocation { get; init; }
    public          object? Value      { get; init; }
}

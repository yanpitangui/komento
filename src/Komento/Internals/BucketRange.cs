namespace Komento.Internals;

internal readonly struct BucketRange
{
    public int Start { get; init; }
    public int End   { get; init; }

    public bool Contains(int bucket) => bucket >= Start && bucket <= End;
}

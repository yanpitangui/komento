using BenchmarkDotNet.Attributes;
using Komento;

/// <summary>
/// Benchmarks for the BinSet segment store (binary search on sorted XxHash64 hashes).
/// </summary>
[MemoryDiagnoser]
public class BinSetBenchmarks
{
    private ReadOnlyMemory<byte> _smallSet  = ReadOnlyMemory<byte>.Empty;  // 100 members
    private ReadOnlyMemory<byte> _largeSet  = ReadOnlyMemory<byte>.Empty;  // 100 000 members

    private string _memberSubject    = null!;  // guaranteed in set
    private string _nonMemberSubject = null!;  // guaranteed not in set

    [GlobalSetup]
    public void Setup()
    {
        var smallIds = Enumerable.Range(0, 100)
            .Select(i => $"user-{i:D6}")
            .ToList();

        var largeIds = Enumerable.Range(0, 100_000)
            .Select(i => $"user-{i:D6}")
            .ToList();

        _smallSet  = BinSet.Build(smallIds);
        _largeSet  = BinSet.Build(largeIds);

        _memberSubject    = "user-000042";   // in both sets
        _nonMemberSubject = "user-999999";   // in neither set
    }

    [Benchmark(Baseline = true)]
    public bool SmallSet_Hit()   => BinSet.Contains(_smallSet,  _memberSubject);

    [Benchmark]
    public bool SmallSet_Miss()  => BinSet.Contains(_smallSet,  _nonMemberSubject);

    [Benchmark]
    public bool LargeSet_Hit()   => BinSet.Contains(_largeSet,  _memberSubject);

    [Benchmark]
    public bool LargeSet_Miss()  => BinSet.Contains(_largeSet,  _nonMemberSubject);
}

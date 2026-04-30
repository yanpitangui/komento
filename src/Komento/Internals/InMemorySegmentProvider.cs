using System.Collections.Frozen;

namespace Komento.Internals;

internal sealed class InMemorySegmentProvider : ISegmentProvider
{
    private readonly FrozenDictionary<string, ReadOnlyMemory<byte>> _segments;

    public InMemorySegmentProvider(IReadOnlyDictionary<string, IEnumerable<string>> segments)
    {
        var dict = new Dictionary<string, ReadOnlyMemory<byte>>(segments.Count, StringComparer.Ordinal);
        foreach (var (segmentName, ids) in segments)
            dict[segmentName] = BinSet.Build(ids);
        _segments = dict.ToFrozenDictionary(StringComparer.Ordinal);
    }

    public ValueTask<bool> IsInSegmentAsync(string subjectId, string segmentName, CancellationToken ct = default)
    {
        if (!_segments.TryGetValue(segmentName, out var binSet))
            return ValueTask.FromResult(false);
        return ValueTask.FromResult(BinSet.Contains(binSet, subjectId));
    }
}

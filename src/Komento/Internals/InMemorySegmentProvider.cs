using System.Collections.Frozen;
using Komento;

namespace Komento;

/// <summary>
/// Segment membership from lists held in memory. Each list is stored as a <see cref="BinSet"/>,
/// so a lookup is a binary search with no allocation.
/// </summary>
public sealed class InMemorySegmentProvider : ISegmentProvider
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

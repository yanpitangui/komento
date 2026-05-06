using System.IO.Hashing;
using System.Text;

namespace Komento;

public static class BinSet
{
    private const int EntrySize = 8; // XxHash64 → 8 bytes per entry

    public static ReadOnlyMemory<byte> Build(IEnumerable<string> ids)
    {
        var hashes = new HashSet<ulong>();
        foreach (var id in ids)
            hashes.Add(HashId(id));

        var sorted = hashes.Order().ToArray();
        var bytes  = new byte[sorted.Length * EntrySize];
        for (var i = 0; i < sorted.Length; i++)
            BitConverter.TryWriteBytes(bytes.AsSpan(i * EntrySize, EntrySize), sorted[i]);

        return bytes;
    }

    public static bool Contains(ReadOnlyMemory<byte> binSet, string id)
    {
        if (binSet.IsEmpty) return false;
        var target = HashId(id);
        return BinarySearch(binSet.Span, target);
    }

    private static ulong HashId(string id)
    {
        var maxBytes = Encoding.UTF8.GetMaxByteCount(id.Length);
        if (maxBytes <= 256)
        {
            Span<byte> stackBuf = stackalloc byte[maxBytes];
            var written = Encoding.UTF8.GetBytes(id, stackBuf);
            return XxHash64.HashToUInt64(stackBuf[..written]);
        }

        var heapBuf = Encoding.UTF8.GetBytes(id);
        return XxHash64.HashToUInt64(heapBuf);
    }

    private static bool BinarySearch(ReadOnlySpan<byte> data, ulong target)
    {
        var count = data.Length / EntrySize;
        var lo    = 0;
        var hi    = count - 1;

        while (lo <= hi)
        {
            var mid   = (lo + hi) >> 1;
            var entry = BitConverter.ToUInt64(data.Slice(mid * EntrySize, EntrySize));
            if (entry == target) return true;
            if (entry < target)  lo = mid + 1;
            else                 hi = mid - 1;
        }
        return false;
    }
}

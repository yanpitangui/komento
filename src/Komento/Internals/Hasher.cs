using System.Buffers;
using System.IO.Hashing;
using System.Text;

namespace Komento.Internals;

internal static class Hasher
{
    private const int  BucketCount    = 1000;
    private const int  StackThreshold = 512;
    private const byte Separator      = (byte)':';

    public static int ComputeBucket(string experimentId, string subjectId)
    {
        var maxBytes = Encoding.UTF8.GetMaxByteCount(experimentId.Length + 1 + subjectId.Length);

        if (maxBytes <= StackThreshold)
        {
            Span<byte> buffer = stackalloc byte[maxBytes];
            var hash = HashSpan(experimentId, subjectId, buffer);
            return (int)(hash % (uint)BucketCount) + 1;
        }

        var rented = ArrayPool<byte>.Shared.Rent(maxBytes);
        try
        {
            var hash = HashSpan(experimentId, subjectId, rented.AsSpan(0, maxBytes));
            return (int)(hash % (uint)BucketCount) + 1;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    private static ulong HashSpan(string experimentId, string subjectId, Span<byte> buffer)
    {
        var written = Encoding.UTF8.GetBytes(experimentId, buffer);
        buffer[written++] = Separator;
        written += Encoding.UTF8.GetBytes(subjectId, buffer[written..]);
        return XxHash64.HashToUInt64(buffer[..written]);
    }
}

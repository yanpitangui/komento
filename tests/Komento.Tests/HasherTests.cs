using AwesomeAssertions;
using Komento.Internals;
using TUnit.Core;

namespace Komento.Tests;

public class HasherTests
{
    [Test]
    public void Same_inputs_always_produce_same_bucket()
    {
        var b1 = Hasher.ComputeBucket("exp-123", "user-42");
        var b2 = Hasher.ComputeBucket("exp-123", "user-42");
        b1.Should().Be(b2);
    }

    [Test]
    public void Bucket_is_within_valid_range()
    {
        for (var i = 0; i < 100; i++)
        {
            var bucket = Hasher.ComputeBucket("exp-abc", $"user-{i}");
            bucket.Should().BeInRange(1, 1000);
        }
    }

    [Test]
    public void Different_experiments_produce_different_buckets_for_same_subject()
    {
        var buckets = Enumerable.Range(0, 10)
            .Select(i => Hasher.ComputeBucket($"exp-{i}", "user-42"))
            .ToHashSet();
        (buckets.Count > 1).Should().BeTrue();
    }

    [Test]
    public void Distribution_is_roughly_uniform_over_large_population()
    {
        const int subjects = 10_000;
        var       counts   = new int[1000];
        for (var i = 0; i < subjects; i++)
        {
            var bucket = Hasher.ComputeBucket("exp-dist", $"user-{i}");
            counts[bucket - 1]++;
        }
        foreach (var count in counts)
            count.Should().BeInRange(1, 50);
    }
}

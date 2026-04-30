using AwesomeAssertions;
using Komento.Internals;
using Xunit;

namespace Komento.Tests;

public class BinSetTests
{
    [Fact]
    public void Contains_returns_true_for_known_id()
    {
        var binSet = BinSet.Build(["user-1", "user-2", "user-3"]);
        BinSet.Contains(binSet, "user-2").Should().BeTrue();
    }

    [Fact]
    public void Contains_returns_false_for_unknown_id()
    {
        var binSet = BinSet.Build(["user-1", "user-2"]);
        BinSet.Contains(binSet, "user-99").Should().BeFalse();
    }

    [Fact]
    public void Empty_binset_never_contains_anything()
    {
        var binSet = BinSet.Build([]);
        BinSet.Contains(binSet, "user-1").Should().BeFalse();
    }

    [Fact]
    public void Single_entry_found()
    {
        var binSet = BinSet.Build(["only-user"]);
        BinSet.Contains(binSet, "only-user").Should().BeTrue();
    }

    [Fact]
    public void Duplicate_ids_produce_single_entry()
    {
        var binSet = BinSet.Build(["a", "a", "a"]);
        binSet.Length.Should().Be(8);
        BinSet.Contains(binSet, "a").Should().BeTrue();
    }

    [Fact]
    public void Large_set_membership_is_correct()
    {
        var ids    = Enumerable.Range(0, 1000).Select(i => $"user-{i}").ToList();
        var binSet = BinSet.Build(ids);
        BinSet.Contains(binSet, "user-500").Should().BeTrue();
        BinSet.Contains(binSet, "user-9999").Should().BeFalse();
    }
}

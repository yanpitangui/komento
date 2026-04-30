using Komento.Internals;
using Xunit;

namespace Komento.Tests;

public class BinSetTests
{
    [Fact]
    public void Contains_returns_true_for_known_id()
    {
        var binSet = BinSet.Build(["user-1", "user-2", "user-3"]);
        Assert.True(BinSet.Contains(binSet, "user-2"));
    }

    [Fact]
    public void Contains_returns_false_for_unknown_id()
    {
        var binSet = BinSet.Build(["user-1", "user-2"]);
        Assert.False(BinSet.Contains(binSet, "user-99"));
    }

    [Fact]
    public void Empty_binset_never_contains_anything()
    {
        var binSet = BinSet.Build([]);
        Assert.False(BinSet.Contains(binSet, "user-1"));
    }

    [Fact]
    public void Single_entry_found()
    {
        var binSet = BinSet.Build(["only-user"]);
        Assert.True(BinSet.Contains(binSet, "only-user"));
    }

    [Fact]
    public void Duplicate_ids_produce_single_entry()
    {
        var binSet = BinSet.Build(["a", "a", "a"]);
        Assert.Equal(8, binSet.Length);
        Assert.True(BinSet.Contains(binSet, "a"));
    }

    [Fact]
    public void Large_set_membership_is_correct()
    {
        var ids    = Enumerable.Range(0, 1000).Select(i => $"user-{i}").ToList();
        var binSet = BinSet.Build(ids);
        Assert.True(BinSet.Contains(binSet, "user-500"));
        Assert.False(BinSet.Contains(binSet, "user-9999"));
    }
}

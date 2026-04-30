using AwesomeAssertions;
using Komento;
using Komento.Internals;
using Xunit;

namespace Komento.Tests;

public class InMemorySegmentProviderTests
{
    private static ISegmentProvider Build(Dictionary<string, IEnumerable<string>> segments)
        => new InMemorySegmentProvider(segments);

    [Fact]
    public async Task Known_subject_in_known_segment_returns_true()
    {
        var provider = Build(new() { ["vip"] = ["user-1", "user-2"] });
        (await provider.IsInSegmentAsync("user-1", "vip")).Should().BeTrue();
    }

    [Fact]
    public async Task Unknown_subject_returns_false()
    {
        var provider = Build(new() { ["vip"] = ["user-1"] });
        (await provider.IsInSegmentAsync("user-99", "vip")).Should().BeFalse();
    }

    [Fact]
    public async Task Unknown_segment_returns_false()
    {
        var provider = Build(new() { ["vip"] = ["user-1"] });
        (await provider.IsInSegmentAsync("user-1", "non-existent-segment")).Should().BeFalse();
    }

    [Fact]
    public async Task Empty_segment_always_returns_false()
    {
        var provider = Build(new() { ["empty"] = [] });
        (await provider.IsInSegmentAsync("user-1", "empty")).Should().BeFalse();
    }
}

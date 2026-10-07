using AwesomeAssertions;
using Komento;
using Microsoft.Extensions.DependencyInjection;
using TUnit.Core;

namespace Komento.Tests;

public class InMemorySegmentProviderTests
{
    [Test]
    public async Task Registered_by_instance_is_resolved_from_the_container()
    {
        var services = new ServiceCollection();
        services.AddKomento().AddSegmentProvider(Build(new() { ["vip"] = ["user-1"] }));

        using var provider = services.BuildServiceProvider();
        var segments = provider.GetRequiredService<ISegmentProvider>();

        (await segments.IsInSegmentAsync("user-1", "vip")).Should().BeTrue();
    }

    private static ISegmentProvider Build(Dictionary<string, IEnumerable<string>> segments)
        => new InMemorySegmentProvider(segments);

    [Test]
    public async Task Known_subject_in_known_segment_returns_true()
    {
        var provider = Build(new() { ["vip"] = ["user-1", "user-2"] });
        (await provider.IsInSegmentAsync("user-1", "vip")).Should().BeTrue();
    }

    [Test]
    public async Task Unknown_subject_returns_false()
    {
        var provider = Build(new() { ["vip"] = ["user-1"] });
        (await provider.IsInSegmentAsync("user-99", "vip")).Should().BeFalse();
    }

    [Test]
    public async Task Unknown_segment_returns_false()
    {
        var provider = Build(new() { ["vip"] = ["user-1"] });
        (await provider.IsInSegmentAsync("user-1", "non-existent-segment")).Should().BeFalse();
    }

    [Test]
    public async Task Empty_segment_always_returns_false()
    {
        var provider = Build(new() { ["empty"] = [] });
        (await provider.IsInSegmentAsync("user-1", "empty")).Should().BeFalse();
    }
}

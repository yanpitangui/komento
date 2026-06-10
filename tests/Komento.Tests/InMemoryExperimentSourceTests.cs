using AwesomeAssertions;
using TUnit.Core;

namespace Komento.Tests;

public class InMemoryExperimentSourceTests
{
    private static ExperimentConfig MakeConfig(string id) => new()
    {
        Id          = id,
        SubjectType = "user",
        Variants    = [new VariantConfig { Name = "control", Allocation = 1.0 }]
    };

    [Test]
    public async Task LoadAsync_returns_only_requested_ids()
    {
        var source = new InMemoryExperimentSource()
            .Set(MakeConfig("exp-a"))
            .Set(MakeConfig("exp-b"));

        var result = await source.LoadAsync(new HashSet<string> { "exp-a" });

        result.Should().ContainSingle();
        result.ContainsKey("exp-a").Should().BeTrue();
        result.ContainsKey("exp-b").Should().BeFalse();
    }

    [Test]
    public async Task LoadAsync_empty_set_returns_all()
    {
        var source = new InMemoryExperimentSource()
            .Set(MakeConfig("exp-a"))
            .Set(MakeConfig("exp-b"));

        var result = await source.LoadAsync(new HashSet<string>());

        result.Count.Should().Be(2);
    }

    [Test]
    public async Task Set_overwrites_existing_config()
    {
        var source = new InMemoryExperimentSource().Set(MakeConfig("exp-a"));

        var updated = new ExperimentConfig
        {
            Id          = "exp-a",
            SubjectType = "user",
            Variants    = [new VariantConfig { Name = "treatment", Allocation = 1.0 }]
        };
        source.Set(updated);

        var result = await source.LoadAsync(new HashSet<string> { "exp-a" });
        result["exp-a"].Variants[0].Name.Should().Be("treatment");
    }

    [Test]
    public async Task Remove_drops_experiment()
    {
        var source = new InMemoryExperimentSource()
            .Set(MakeConfig("exp-a"))
            .Set(MakeConfig("exp-b"))
            .Remove("exp-a");

        var result = await source.LoadAsync(new HashSet<string>());

        result.ContainsKey("exp-a").Should().BeFalse();
        result.ContainsKey("exp-b").Should().BeTrue();
    }

    [Test]
    public async Task LoadAsync_returns_empty_when_no_configs_set()
    {
        var source = new InMemoryExperimentSource();
        var result = await source.LoadAsync(new HashSet<string> { "anything" });
        result.Should().BeEmpty();
    }
}

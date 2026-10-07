using AwesomeAssertions;
using Komento.Internals;
using TUnit.Core;

namespace Komento.Tests;

public class ConfigChangesTests
{
    private static ExperimentConfig Config(string id, double control = 0.5) => new()
    {
        Id          = id,
        SubjectType = "user",
        Variants    =
        [
            new VariantConfig { Name = "control",   Allocation = control },
            new VariantConfig { Name = "treatment", Allocation = 1.0 - control }
        ]
    };

    private static (ExperimentClient Client, List<string[]> Reports) Build()
    {
        var client  = new ExperimentClient(new KomentoOptions());
        var reports = new List<string[]>();
        ((IConfigChanges)client).Changed += ids => reports.Add(ids.OrderBy(i => i).ToArray());
        return (client, reports);
    }

    [Test]
    public async Task Adding_an_experiment_reports_it()
    {
        var (client, reports) = Build();

        await client.UpdateAsync(Config("a"));

        reports.Should().ContainSingle().Which.Should().Equal("a");
    }

    [Test]
    public async Task Updating_with_an_identical_config_reports_nothing()
    {
        var (client, reports) = Build();
        await client.UpdateAsync(Config("a"));
        reports.Clear();

        await client.UpdateAsync(Config("a"));   // a separately built, identical config

        reports.Should().BeEmpty();
    }

    [Test]
    public async Task Updating_with_a_changed_config_reports_only_that_experiment()
    {
        var (client, reports) = Build();
        await client.UpdateAsync(new Dictionary<string, ExperimentConfig> { ["a"] = Config("a"), ["b"] = Config("b") });
        reports.Clear();

        await client.UpdateAsync(new Dictionary<string, ExperimentConfig> { ["a"] = Config("a"), ["b"] = Config("b", control: 0.9) });

        reports.Should().ContainSingle().Which.Should().Equal("b");
    }

    [Test]
    public async Task Removing_an_experiment_reports_it_and_removing_an_unknown_one_reports_nothing()
    {
        var (client, reports) = Build();
        await client.UpdateAsync(Config("a"));
        reports.Clear();

        await client.RemoveAsync("a");
        await client.RemoveAsync("never-existed");

        reports.Should().ContainSingle().Which.Should().Equal("a");
    }

    [Test]
    public async Task A_filtered_update_reports_only_the_experiments_it_applied()
    {
        var (client, reports) = Build();

        await client.UpdateAsync(
            new Dictionary<string, ExperimentConfig> { ["a"] = Config("a"), ["b"] = Config("b") },
            new HashSet<string> { "a" });

        reports.Should().ContainSingle().Which.Should().Equal("a");
    }

    [Test]
    public async Task A_subscriber_that_throws_does_not_break_the_update()
    {
        var client = new ExperimentClient(new KomentoOptions());
        ((IConfigChanges)client).Changed += _ => throw new InvalidOperationException("boom");

        await client.UpdateAsync(Config("a"));

        client.ExperimentExists("a").Should().BeTrue();
    }
}

using System.Text;
using AwesomeAssertions;
using Komento.Internals;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TUnit.Core;

namespace Komento.Tests;

public class ExposureContextTests
{
    private static ExperimentConfig Experiment(
        string[]? exposureContext = null,
        IReadOnlyList<FilterConfig>? filters = null) => new()
    {
        Id              = "context-exp",
        SubjectType     = "user",
        Variants        = [new VariantConfig { Name = "only", Allocation = 1.0 }],
        GlobalFilters   = filters ?? [],
        ExposureContext = exposureContext ?? []
    };

    private static ServiceProvider Build(EvaluationContext? staticContext = null, ISegmentProvider? segments = null)
    {
        var services = new ServiceCollection();
        var builder  = services.AddKomento(o =>
        {
            o.EnableExposureStream = true;
            if (staticContext is { } s) o.StaticContext = s;
        });
        if (segments is not null) builder.AddSegmentProvider(segments);
        return services.BuildServiceProvider();
    }

    private static async Task<ExposureEvent> Evaluate(
        ServiceProvider provider, ExperimentConfig config, EvaluationContext ctx)
    {
        await provider.GetRequiredService<IConfigUpdater>().UpdateAsync(config);
        await provider.GetRequiredService<IExperimentClient>().GetVariantAsync(config.Id, "user-1", ctx);

        provider.GetRequiredService<IExposureStream>().Reader.TryRead(out var exposure).Should().BeTrue();
        return exposure;
    }

    private static EvaluationContext Ctx(params (string Key, object Value)[] attributes)
    {
        var builder = EvaluationContext.Create();
        foreach (var (key, value) in attributes) builder.Set(key, value);
        return builder.Build();
    }

    [Test]
    public async Task Without_listed_keys_the_exposure_has_no_context()
    {
        using var provider = Build();

        var exposure = await Evaluate(provider, Experiment(), Ctx(("country", "BR")));

        exposure.Context.Should().BeNull();
    }

    [Test]
    public async Task Listed_keys_are_copied_from_the_call_context()
    {
        using var provider = Build();

        var exposure = await Evaluate(provider, Experiment(["country", "plan"]),
            Ctx(("country", "BR"), ("plan", "premium"), ("email", "a@b.c")));

        exposure.Context.Should().BeEquivalentTo(new Dictionary<string, object>
        {
            ["country"] = "BR",
            ["plan"]    = "premium"
        });
    }

    [Test]
    public async Task Unlisted_attributes_are_left_off()
    {
        using var provider = Build();

        var exposure = await Evaluate(provider, Experiment(["country"]),
            Ctx(("country", "BR"), ("email", "a@b.c")));

        exposure.Context.Should().NotContainKey("email");
    }

    [Test]
    public async Task Listed_keys_missing_from_the_context_are_omitted()
    {
        using var provider = Build();

        var exposure = await Evaluate(provider, Experiment(["country", "plan"]), Ctx(("country", "BR")));

        exposure.Context.Should().BeEquivalentTo(new Dictionary<string, object> { ["country"] = "BR" });
    }

    [Test]
    public async Task No_listed_key_present_gives_no_context()
    {
        using var provider = Build();

        var exposure = await Evaluate(provider, Experiment(["plan"]), Ctx(("country", "BR")));

        exposure.Context.Should().BeNull();
    }

    [Test]
    public async Task Static_context_supplies_listed_keys()
    {
        using var provider = Build(staticContext: Ctx(("region", "BR")));

        var exposure = await Evaluate(provider, Experiment(["region"]), EvaluationContext.Empty);

        exposure.Context.Should().BeEquivalentTo(new Dictionary<string, object> { ["region"] = "BR" });
    }

    [Test]
    public async Task Per_call_attributes_win_over_static_ones()
    {
        using var provider = Build(staticContext: Ctx(("region", "BR")));

        var exposure = await Evaluate(provider, Experiment(["region"]), Ctx(("region", "US")));

        exposure.Context.Should().BeEquivalentTo(new Dictionary<string, object> { ["region"] = "US" });
    }

    [Test]
    public async Task Ineligible_exposures_carry_the_context_too()
    {
        using var provider = Build();
        var filters = new FilterConfig[] { new TraitEqualsFilter { Key = "country", Value = "BR" } };

        var exposure = await Evaluate(provider, Experiment(["country"], filters), Ctx(("country", "US")));

        exposure.IsEligible.Should().BeFalse();
        exposure.Context.Should().BeEquivalentTo(new Dictionary<string, object> { ["country"] = "US" });
    }

    [Test]
    public async Task The_asynchronous_path_carries_the_context()
    {
        var segments = new InMemorySegmentProvider(new Dictionary<string, IEnumerable<string>> { ["beta"] = ["user-1"] });
        using var provider = Build(segments: segments);
        var filters = new FilterConfig[] { new SegmentIncludeFilter { Segment = "beta" } };

        var exposure = await Evaluate(provider, Experiment(["plan"], filters), Ctx(("plan", "premium")));

        exposure.IsEligible.Should().BeTrue();
        exposure.Context.Should().BeEquivalentTo(new Dictionary<string, object> { ["plan"] = "premium" });
    }

    [Test]
    public async Task Changing_the_listed_keys_changes_the_revision()
    {
        using var first  = Build();
        using var second = Build();

        var a = await Evaluate(first,  Experiment(["country"]), EvaluationContext.Empty);
        var b = await Evaluate(second, Experiment(["country", "plan"]), EvaluationContext.Empty);

        b.ConfigRevision.Should().NotBe(a.ConfigRevision);
    }

    [Test]
    public async Task Swapping_a_listed_key_changes_the_revision()
    {
        using var first  = Build();
        using var second = Build();

        var a = await Evaluate(first,  Experiment(["country"]), EvaluationContext.Empty);
        var b = await Evaluate(second, Experiment(["plan"]),    EvaluationContext.Empty);

        b.ConfigRevision.Should().NotBe(a.ConfigRevision);
    }

    [Test]
    public async Task Changing_the_listed_keys_is_reported_as_a_change()
    {
        var client  = new ExperimentClient(new KomentoOptions());
        var reports = new List<string[]>();
        ((IConfigChanges)client).Changed += ids => reports.Add(ids.ToArray());
        await client.UpdateAsync(Experiment(["country"]));
        reports.Clear();

        await client.UpdateAsync(Experiment(["country", "plan"]));

        reports.Should().ContainSingle().Which.Should().Equal("context-exp");
    }

    [Test]
    public async Task The_appsettings_source_reads_the_listed_keys()
    {
        const string json = """
        {
          "Komento": {
            "Experiments": [
              {
                "Id": "context-exp",
                "SubjectType": "user",
                "Variants": [ { "Name": "only", "Allocation": 1.0 } ],
                "ExposureContext": [ "country", "plan" ]
              }
            ]
          }
        }
        """;
        var configuration = new ConfigurationBuilder()
            .AddJsonStream(new MemoryStream(Encoding.UTF8.GetBytes(json)))
            .Build();

        var loaded = await new AppSettingsExperimentSource(configuration).LoadAsync(new HashSet<string>());

        loaded["context-exp"].ExposureContext.Should().Equal("country", "plan");
    }
}

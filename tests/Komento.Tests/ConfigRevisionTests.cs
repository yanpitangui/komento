using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using TUnit.Core;

namespace Komento.Tests;

public class ConfigRevisionTests
{
    private static ExperimentConfig Experiment(
        double treatmentAllocation = 0.5,
        object? treatmentValue = null,
        IReadOnlyList<FilterConfig>? filters = null,
        IReadOnlyList<OverrideRule>? overrides = null,
        string subjectType = "user") => new()
    {
        Id          = "revision-exp",
        SubjectType = subjectType,
        Variants    =
        [
            new VariantConfig { Name = "control",   Allocation = 0.5 },
            new VariantConfig { Name = "treatment", Allocation = treatmentAllocation, Value = treatmentValue }
        ],
        GlobalFilters = filters   ?? [],
        Overrides     = overrides ?? []
    };

    private static async Task<string?> RevisionAfter(ExperimentConfig config, ServiceProvider? existing = null)
    {
        using var owned = existing is null ? Build() : null;
        var provider    = existing ?? owned!;

        await provider.GetRequiredService<IConfigUpdater>().UpdateAsync(config);
        await provider.GetRequiredService<IExperimentClient>()
            .GetVariantAsync(config.Id, "user-1", EvaluationContext.Empty);

        provider.GetRequiredService<IExposureStream>().Reader.TryRead(out var exposure).Should().BeTrue();
        return exposure.ConfigRevision;
    }

    private static ServiceProvider Build()
    {
        var services = new ServiceCollection();
        services.AddKomento(o => o.EnableExposureStream = true);
        return services.BuildServiceProvider();
    }

    [Test]
    public async Task Exposure_carries_a_revision()
    {
        var revision = await RevisionAfter(Experiment());

        revision.Should().NotBeNullOrEmpty();
    }

    [Test]
    public async Task Identical_configs_built_separately_have_the_same_revision()
    {
        var first  = await RevisionAfter(Experiment(treatmentValue: true));
        var second = await RevisionAfter(Experiment(treatmentValue: true));

        second.Should().Be(first);
    }

    [Test]
    public async Task Reapplying_the_same_config_keeps_the_revision()
    {
        using var provider = Build();
        var first  = await RevisionAfter(Experiment(), provider);
        var second = await RevisionAfter(Experiment(), provider);

        second.Should().Be(first);
    }

    [Test]
    public async Task Changing_an_allocation_changes_the_revision()
        => (await RevisionAfter(Experiment(treatmentAllocation: 0.4)))
            .Should().NotBe(await RevisionAfter(Experiment()));

    [Test]
    public async Task Changing_a_variant_value_changes_the_revision()
        => (await RevisionAfter(Experiment(treatmentValue: "a")))
            .Should().NotBe(await RevisionAfter(Experiment(treatmentValue: "b")));

    [Test]
    public async Task Changing_the_subject_type_changes_the_revision()
        => (await RevisionAfter(Experiment(subjectType: "device")))
            .Should().NotBe(await RevisionAfter(Experiment()));

    [Test]
    public async Task Adding_a_subject_override_changes_the_revision()
        => (await RevisionAfter(Experiment(overrides: [new SubjectOverride { SubjectId = "user-9", Variant = "treatment" }])))
            .Should().NotBe(await RevisionAfter(Experiment()));

    [Test]
    public async Task Changing_the_subject_of_an_override_changes_the_revision()
        => (await RevisionAfter(Experiment(overrides: [new SubjectOverride { SubjectId = "user-9", Variant = "treatment" }])))
            .Should().NotBe(await RevisionAfter(Experiment(overrides: [new SubjectOverride { SubjectId = "user-8", Variant = "treatment" }])));

    [Test]
    public async Task Changing_the_variant_of_an_override_changes_the_revision()
        => (await RevisionAfter(Experiment(overrides: [new SegmentOverride { Segment = "beta", Variant = "treatment" }])))
            .Should().NotBe(await RevisionAfter(Experiment(overrides: [new SegmentOverride { Segment = "beta", Variant = "control" }])));

    [Test]
    public async Task Changing_a_filter_changes_the_revision()
        => (await RevisionAfter(Experiment(filters: [new TraitEqualsFilter { Key = "country", Value = "BR" }])))
            .Should().NotBe(await RevisionAfter(Experiment(filters: [new TraitEqualsFilter { Key = "country", Value = "US" }])));

    [Test]
    public async Task A_filter_and_an_override_with_the_same_text_do_not_collide()
        => (await RevisionAfter(Experiment(filters: [new SegmentIncludeFilter { Segment = "beta" }])))
            .Should().NotBe(await RevisionAfter(Experiment(overrides: [new SegmentOverride { Segment = "beta", Variant = "" }])));

    [Test]
    public async Task Updating_the_config_changes_the_revision_on_later_exposures()
    {
        using var provider = Build();
        var before = await RevisionAfter(Experiment(), provider);
        var after  = await RevisionAfter(Experiment(overrides: [new SubjectOverride { SubjectId = "user-9", Variant = "treatment" }]), provider);

        after.Should().NotBe(before);
    }
}

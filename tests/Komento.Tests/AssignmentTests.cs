using AwesomeAssertions;
using Komento.Internals;
using TUnit.Core;

namespace Komento.Tests;

public class AssignmentTests
{
    private sealed class AlwaysInSegment : ISegmentProvider
    {
        public ValueTask<bool> IsInSegmentAsync(string subjectId, string segmentName, CancellationToken ct = default)
            => ValueTask.FromResult(true);
    }

    private static async Task<ExperimentClient> BuildAsync(ExperimentConfig config, ISegmentProvider? segments = null)
    {
        var client = new ExperimentClient(new KomentoOptions(), segments);
        await client.UpdateAsync(config);
        return client;
    }

    private static ExperimentConfig Config(
        double allocation = 1.0,
        IReadOnlyList<FilterConfig>? filters = null,
        IReadOnlyList<OverrideRule>? overrides = null) => new()
    {
        Id            = "assignment-exp",
        SubjectType   = "device",
        Variants      =
        [
            new VariantConfig { Name = "control",   Allocation = 1.0 - allocation },
            new VariantConfig { Name = "treatment", Allocation = allocation }
        ],
        GlobalFilters = filters   ?? [],
        Overrides     = overrides ?? []
    };

    [Test]
    public async Task Hash_assignment_reports_the_hash_source_and_the_subject_type()
    {
        var client = await BuildAsync(Config(allocation: 1.0));

        var result = await client.GetVariantAsync("assignment-exp", "subject-1", EvaluationContext.Empty);

        (result == "treatment").Should().BeTrue();
        result.Source.Should().Be(AssignmentSource.Hash);
        result.SubjectType.Should().Be("device");
    }

    [Test]
    public async Task Subject_override_reports_the_subject_override_source()
    {
        var client = await BuildAsync(Config(overrides: [new SubjectOverride { SubjectId = "vip", Variant = "treatment" }]));

        var result = await client.GetVariantAsync("assignment-exp", "vip", EvaluationContext.Empty);

        result.Source.Should().Be(AssignmentSource.SubjectOverride);
        result.SubjectType.Should().Be("device");
    }

    [Test]
    public async Task Segment_override_reports_the_segment_override_source()
    {
        var client = await BuildAsync(
            Config(overrides: [new SegmentOverride { Segment = "beta", Variant = "treatment" }]),
            new AlwaysInSegment());

        var result = await client.GetVariantAsync("assignment-exp", "subject-1", EvaluationContext.Empty);

        result.Source.Should().Be(AssignmentSource.SegmentOverride);
    }

    [Test]
    public async Task Ineligible_result_has_no_source_but_carries_the_subject_type()
    {
        var client = await BuildAsync(Config(filters: [new TraitEqualsFilter { Key = "region", Value = "eu" }]));

        var result = await client.GetVariantAsync("assignment-exp", "subject-1", EvaluationContext.Empty);

        result.IsEligible.Should().BeFalse();
        result.Source.Should().Be(AssignmentSource.None);
        result.SubjectType.Should().Be("device");
    }

    [Test]
    public async Task Outsider_result_has_no_source_but_carries_the_subject_type()
    {
        // A single variant with no allocation: every subject falls outside it.
        var client = await BuildAsync(new ExperimentConfig
        {
            Id          = "assignment-exp",
            SubjectType = "device",
            Variants    = [new VariantConfig { Name = "treatment", Allocation = 0.0 }]
        });

        var result = await client.GetVariantAsync("assignment-exp", "subject-1", EvaluationContext.Empty);

        result.IsOutsider.Should().BeTrue();
        result.Source.Should().Be(AssignmentSource.None);
        result.SubjectType.Should().Be("device");
    }

    [Test]
    public async Task Unknown_experiment_has_no_source_and_no_subject_type()
    {
        var client = await BuildAsync(Config());

        var result = await client.GetVariantAsync("does-not-exist", "subject-1", EvaluationContext.Empty);

        result.Should().Be(VariantResult.NotFound);
        result.Source.Should().Be(AssignmentSource.None);
        result.SubjectType.Should().BeNull();
    }
}

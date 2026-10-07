using AwesomeAssertions;
using Komento.Internals;
using TUnit.Core;

namespace Komento.Tests;

public class StaticContextTests
{
    private sealed class AlwaysInSegment : ISegmentProvider
    {
        public ValueTask<bool> IsInSegmentAsync(string subjectId, string segmentName, CancellationToken ct = default)
            => ValueTask.FromResult(true);
    }

    private static EvaluationContext Region(string value)
        => EvaluationContext.Create().Set("region", value).Build();

    private static ExperimentConfig RegionExperiment(string id, string region, params FilterConfig[] more) => new()
    {
        Id            = id,
        SubjectType   = "user",
        Variants      = [new VariantConfig { Name = "only", Allocation = 1.0 }],
        GlobalFilters = [new TraitEqualsFilter { Key = "region", Value = region }, .. more]
    };

    private static async Task<ExperimentClient> BuildAsync(
        EvaluationContext staticContext, ExperimentConfig config, ISegmentProvider? segments = null)
    {
        var client = new ExperimentClient(new KomentoOptions { StaticContext = staticContext }, segments);
        await client.UpdateAsync(config);
        return client;
    }

    [Test]
    public async Task Static_context_attribute_satisfies_a_trait_filter()
    {
        var client = await BuildAsync(Region("eu"), RegionExperiment("static-sync", "eu"));

        var result = await client.GetVariantAsync("static-sync", "user-1", EvaluationContext.Empty);

        result.IsEligible.Should().BeTrue();
        (result == "only").Should().BeTrue();
    }

    [Test]
    public async Task Static_context_applies_on_the_async_segment_path()
    {
        var config = RegionExperiment("static-async", "eu", new SegmentIncludeFilter { Segment = "any" });
        var client = await BuildAsync(Region("eu"), config, new AlwaysInSegment());

        var result = await client.GetVariantAsync("static-async", "user-1", EvaluationContext.Empty);

        result.IsEligible.Should().BeTrue();
        (result == "only").Should().BeTrue();
    }

    [Test]
    public async Task Per_call_attribute_takes_precedence_over_the_static_value()
    {
        // Static says "eu"; the call says "us". The call wins.
        var matchesCall   = await BuildAsync(Region("eu"), RegionExperiment("precedence-us", "us"));
        var matchesStatic = await BuildAsync(Region("eu"), RegionExperiment("precedence-eu", "eu"));

        var us = await matchesCall.GetVariantAsync("precedence-us", "user-1", Region("us"));
        var eu = await matchesStatic.GetVariantAsync("precedence-eu", "user-1", Region("us"));

        us.IsEligible.Should().BeTrue();
        eu.IsEligible.Should().BeFalse();
    }

    [Test]
    public async Task Attribute_in_neither_context_leaves_the_subject_ineligible()
    {
        var client = await BuildAsync(EvaluationContext.Empty, RegionExperiment("static-none", "eu"));

        var result = await client.GetVariantAsync("static-none", "user-1", EvaluationContext.Empty);

        result.IsEligible.Should().BeFalse();
    }
}

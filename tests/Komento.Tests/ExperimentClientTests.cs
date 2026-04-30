using AwesomeAssertions;
using Komento;
using Komento.Internals;
using Xunit;

namespace Komento.Tests;

public class ExperimentClientTests
{
    private static ExperimentConfig FiftyFifty(string id = "exp-1") => new()
    {
        Id          = id,
        SubjectType = "user",
        Variants    =
        [
            new VariantConfig { Name = "control",   Allocation = 0.5 },
            new VariantConfig { Name = "treatment",  Allocation = 0.5, Value = true }
        ]
    };

    private static ExperimentClient BuildClient(
        ExperimentConfig? config          = null,
        ISegmentProvider? segmentProvider = null)
    {
        var cfg     = config ?? FiftyFifty();
        var options = new KomentoOptions { Experiments = new HashSet<string> { cfg.Id } };
        var client  = new ExperimentClient(options, segmentProvider);
        client.UpdateAsync(new Dictionary<string, ExperimentConfig> { [cfg.Id] = cfg }).AsTask().Wait();
        return client;
    }

    [Fact]
    public async Task Unknown_experiment_returns_NotFound()
    {
        var client = BuildClient();
        var result = await client.GetVariantAsync("does-not-exist", "user-1", EvaluationContext.Empty);
        result.Should().Be(VariantResult.NotFound);
    }

    [Fact]
    public async Task Same_subject_always_gets_same_variant()
    {
        var client = BuildClient();
        var r1     = await client.GetVariantAsync("exp-1", "user-42", EvaluationContext.Empty);
        var r2     = await client.GetVariantAsync("exp-1", "user-42", EvaluationContext.Empty);
        r1.VariantName.Should().Be(r2.VariantName);
    }

    [Fact]
    public async Task SubjectOverride_forces_variant_regardless_of_hash()
    {
        var config = new ExperimentConfig
        {
            Id          = "exp-override",
            SubjectType = "user",
            Variants    = [new VariantConfig { Name = "control", Allocation = 1.0 }],
            Overrides   = [new SubjectOverride { SubjectId = "vip-user", Variant = "treatment" }]
        };
        var client = BuildClient(config);
        var result = await client.GetVariantAsync("exp-override", "vip-user", EvaluationContext.Empty);
        (result == "treatment").Should().BeTrue();
    }

    [Fact]
    public async Task TraitEqualsFilter_excludes_non_matching_subject()
    {
        var config = new ExperimentConfig
        {
            Id            = "exp-filter",
            SubjectType   = "user",
            Variants      = [new VariantConfig { Name = "control", Allocation = 1.0 }],
            GlobalFilters = [new TraitEqualsFilter { Key = "country", Value = "BR" }]
        };
        var client = BuildClient(config);
        var ctx    = EvaluationContext.Create().Set("country", "US").Build();
        var result = await client.GetVariantAsync("exp-filter", "user-1", in ctx);
        result.Should().Be(VariantResult.Ineligible);
    }

    [Fact]
    public async Task TraitEqualsFilter_allows_matching_subject()
    {
        var config = new ExperimentConfig
        {
            Id            = "exp-filter2",
            SubjectType   = "user",
            Variants      = [new VariantConfig { Name = "control", Allocation = 1.0 }],
            GlobalFilters = [new TraitEqualsFilter { Key = "country", Value = "BR" }]
        };
        var client = BuildClient(config);
        var ctx    = EvaluationContext.Create().Set("country", "BR").Build();
        var result = await client.GetVariantAsync("exp-filter2", "user-1", in ctx);
        result.IsEligible.Should().BeTrue();
    }

    [Fact]
    public async Task SegmentOverride_forces_variant_for_segment_member()
    {
        var segmentProvider = new InMemorySegmentProvider(
            new Dictionary<string, IEnumerable<string>> { ["internal-staff"] = ["employee-1"] });

        var config = new ExperimentConfig
        {
            Id          = "exp-seg-override",
            SubjectType = "user",
            Variants    = [new VariantConfig { Name = "control", Allocation = 1.0 }],
            Overrides   = [new SegmentOverride { Segment = "internal-staff", Variant = "treatment" }]
        };
        var client = BuildClient(config, segmentProvider);
        var result = await client.GetVariantAsync("exp-seg-override", "employee-1", EvaluationContext.Empty);
        (result == "treatment").Should().BeTrue();
    }

    [Fact]
    public async Task SegmentIncludeFilter_excludes_non_member()
    {
        var segmentProvider = new InMemorySegmentProvider(
            new Dictionary<string, IEnumerable<string>> { ["beta"] = ["user-beta"] });

        var config = new ExperimentConfig
        {
            Id            = "exp-seg-filter",
            SubjectType   = "user",
            Variants      = [new VariantConfig { Name = "control", Allocation = 1.0 }],
            GlobalFilters = [new SegmentIncludeFilter { Segment = "beta" }]
        };
        var client = BuildClient(config, segmentProvider);
        var result = await client.GetVariantAsync("exp-seg-filter", "regular-user", EvaluationContext.Empty);
        result.Should().Be(VariantResult.Ineligible);
    }

    [Fact]
    public async Task Subject_not_in_any_bucket_is_outsider()
    {
        var config = new ExperimentConfig
        {
            Id          = "exp-outsider",
            SubjectType = "user",
            Variants    = [new VariantConfig { Name = "treatment", Allocation = 0.0 }]
        };
        var client = BuildClient(config);
        var result = await client.GetVariantAsync("exp-outsider", "user-1", EvaluationContext.Empty);
        result.IsOutsider.Should().BeTrue();
        (result == "control").Should().BeTrue();
    }

    [Fact]
    public async Task Exposure_event_is_written_to_channel()
    {
        var client = BuildClient();
        await client.GetVariantAsync("exp-1", "user-42", EvaluationContext.Empty);

        client.Exposures.TryRead(out var exposure).Should().BeTrue();
        exposure.FlagKey.Should().Be("exp-1");
        exposure.SubjectId.Should().Be("user-42");
    }

    [Fact]
    public async Task GetBoolAsync_returns_default_when_experiment_not_found()
    {
        var client = BuildClient();
        var result = await client.GetBoolAsync("missing", "user-1", EvaluationContext.Empty, defaultValue: true);
        result.Should().BeTrue();
    }

    [Fact]
    public async Task RelevantExperimentIds_reflects_loaded_experiments()
    {
        var client = BuildClient();
        client.RelevantExperimentIds.Should().Contain("exp-1");
    }

    [Fact]
    public async Task UpdateAsync_single_experiment_replaces_it()
    {
        var client    = BuildClient();
        var newConfig = new ExperimentConfig
        {
            Id          = "exp-1",
            SubjectType = "user",
            Variants    = [new VariantConfig { Name = "new-variant", Allocation = 1.0 }]
        };
        await client.UpdateAsync(newConfig);
        var result = await client.GetVariantAsync("exp-1", "user-1", EvaluationContext.Empty);
        (result == "new-variant").Should().BeTrue();
    }

    [Fact]
    public async Task RemoveAsync_removes_experiment()
    {
        var client = BuildClient();
        await client.RemoveAsync("exp-1");
        var result = await client.GetVariantAsync("exp-1", "user-1", EvaluationContext.Empty);
        result.Should().Be(VariantResult.NotFound);
    }
}

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

    // ── Typed helpers ─────────────────────────────────────────────────────────

    [Fact]
    public async Task GetBoolAsync_returns_typed_value_from_variant()
    {
        var config = new ExperimentConfig
        {
            Id          = "bool-flag",
            SubjectType = "user",
            Variants    = [new VariantConfig { Name = "on", Allocation = 1.0, Value = true }]
        };
        var client = BuildClient(config);
        var result = await client.GetBoolAsync("bool-flag", "user-1", EvaluationContext.Empty);
        result.Should().BeTrue();
    }

    [Fact]
    public async Task GetBoolAsync_returns_default_when_ineligible()
    {
        var config = new ExperimentConfig
        {
            Id            = "bool-filtered",
            SubjectType   = "user",
            Variants      = [new VariantConfig { Name = "on", Allocation = 1.0, Value = true }],
            GlobalFilters = [new TraitEqualsFilter { Key = "x", Value = "y" }]
        };
        var client = BuildClient(config);
        (await client.GetBoolAsync("bool-filtered", "user-1", EvaluationContext.Empty, defaultValue: false)).Should().BeFalse();
    }

    [Fact]
    public async Task GetBoolAsync_returns_default_when_value_is_wrong_type()
    {
        var config = new ExperimentConfig
        {
            Id          = "bool-wrong-type",
            SubjectType = "user",
            Variants    = [new VariantConfig { Name = "on", Allocation = 1.0, Value = "not-a-bool" }]
        };
        var client = BuildClient(config);
        (await client.GetBoolAsync("bool-wrong-type", "user-1", EvaluationContext.Empty, defaultValue: true)).Should().BeTrue();
    }

    [Fact]
    public async Task GetStringAsync_returns_typed_value_from_variant()
    {
        var config = new ExperimentConfig
        {
            Id          = "str-flag",
            SubjectType = "user",
            Variants    = [new VariantConfig { Name = "v1", Allocation = 1.0, Value = "hello" }]
        };
        var client = BuildClient(config);
        (await client.GetStringAsync("str-flag", "user-1", EvaluationContext.Empty)).Should().Be("hello");
    }

    [Fact]
    public async Task GetStringAsync_returns_default_when_ineligible()
    {
        var config = new ExperimentConfig
        {
            Id            = "str-filtered",
            SubjectType   = "user",
            Variants      = [new VariantConfig { Name = "v1", Allocation = 1.0, Value = "hello" }],
            GlobalFilters = [new TraitEqualsFilter { Key = "x", Value = "y" }]
        };
        var client = BuildClient(config);
        (await client.GetStringAsync("str-filtered", "user-1", EvaluationContext.Empty, defaultValue: "fallback")).Should().Be("fallback");
    }

    [Fact]
    public async Task GetStringAsync_returns_default_when_value_is_wrong_type()
    {
        var config = new ExperimentConfig
        {
            Id          = "str-wrong-type",
            SubjectType = "user",
            Variants    = [new VariantConfig { Name = "v1", Allocation = 1.0, Value = 42 }]
        };
        var client = BuildClient(config);
        (await client.GetStringAsync("str-wrong-type", "user-1", EvaluationContext.Empty, defaultValue: "fallback")).Should().Be("fallback");
    }

    [Fact]
    public async Task GetIntAsync_returns_typed_value_from_variant()
    {
        var config = new ExperimentConfig
        {
            Id          = "int-flag",
            SubjectType = "user",
            Variants    = [new VariantConfig { Name = "v1", Allocation = 1.0, Value = 42 }]
        };
        var client = BuildClient(config);
        (await client.GetIntAsync("int-flag", "user-1", EvaluationContext.Empty)).Should().Be(42);
    }

    [Fact]
    public async Task GetIntAsync_returns_default_when_ineligible()
    {
        var config = new ExperimentConfig
        {
            Id            = "int-filtered",
            SubjectType   = "user",
            Variants      = [new VariantConfig { Name = "v1", Allocation = 1.0, Value = 42 }],
            GlobalFilters = [new TraitEqualsFilter { Key = "x", Value = "y" }]
        };
        var client = BuildClient(config);
        (await client.GetIntAsync("int-filtered", "user-1", EvaluationContext.Empty, defaultValue: -1)).Should().Be(-1);
    }

    [Fact]
    public async Task GetDoubleAsync_returns_typed_value_from_variant()
    {
        var config = new ExperimentConfig
        {
            Id          = "dbl-flag",
            SubjectType = "user",
            Variants    = [new VariantConfig { Name = "v1", Allocation = 1.0, Value = 3.14 }]
        };
        var client = BuildClient(config);
        (await client.GetDoubleAsync("dbl-flag", "user-1", EvaluationContext.Empty)).Should().Be(3.14);
    }

    [Fact]
    public async Task GetDoubleAsync_returns_default_when_ineligible()
    {
        var config = new ExperimentConfig
        {
            Id            = "dbl-filtered",
            SubjectType   = "user",
            Variants      = [new VariantConfig { Name = "v1", Allocation = 1.0, Value = 3.14 }],
            GlobalFilters = [new TraitEqualsFilter { Key = "x", Value = "y" }]
        };
        var client = BuildClient(config);
        (await client.GetDoubleAsync("dbl-filtered", "user-1", EvaluationContext.Empty, defaultValue: -1.0)).Should().Be(-1.0);
    }

    // ── Async slow path (typed helpers) ───────────────────────────────────────

    // A segment provider that yields to force a truly async ValueTask.
    private sealed class YieldingSegmentProvider(bool isMember) : ISegmentProvider
    {
        public async ValueTask<bool> IsInSegmentAsync(string subjectId, string segmentName, CancellationToken ct)
        {
            await Task.Yield();
            return isMember;
        }
    }

    [Fact]
    public async Task GetBoolAsync_async_path_returns_value()
    {
        var config = new ExperimentConfig
        {
            Id            = "async-bool",
            SubjectType   = "user",
            Variants      = [new VariantConfig { Name = "on", Allocation = 1.0, Value = true }],
            GlobalFilters = [new SegmentIncludeFilter { Segment = "seg" }]
        };
        var client = BuildClient(config, new YieldingSegmentProvider(isMember: true));
        (await client.GetBoolAsync("async-bool", "user-1", EvaluationContext.Empty)).Should().BeTrue();
    }

    [Fact]
    public async Task GetBoolAsync_async_path_returns_default_when_ineligible()
    {
        var config = new ExperimentConfig
        {
            Id            = "async-bool-out",
            SubjectType   = "user",
            Variants      = [new VariantConfig { Name = "on", Allocation = 1.0, Value = true }],
            GlobalFilters = [new SegmentIncludeFilter { Segment = "seg" }]
        };
        var client = BuildClient(config, new YieldingSegmentProvider(isMember: false));
        (await client.GetBoolAsync("async-bool-out", "user-1", EvaluationContext.Empty, defaultValue: false)).Should().BeFalse();
    }

    [Fact]
    public async Task GetStringAsync_async_path_returns_value()
    {
        var config = new ExperimentConfig
        {
            Id            = "async-str",
            SubjectType   = "user",
            Variants      = [new VariantConfig { Name = "v1", Allocation = 1.0, Value = "hello" }],
            GlobalFilters = [new SegmentIncludeFilter { Segment = "seg" }]
        };
        var client = BuildClient(config, new YieldingSegmentProvider(isMember: true));
        (await client.GetStringAsync("async-str", "user-1", EvaluationContext.Empty)).Should().Be("hello");
    }

    [Fact]
    public async Task GetIntAsync_async_path_returns_value()
    {
        var config = new ExperimentConfig
        {
            Id            = "async-int",
            SubjectType   = "user",
            Variants      = [new VariantConfig { Name = "v1", Allocation = 1.0, Value = 7 }],
            GlobalFilters = [new SegmentIncludeFilter { Segment = "seg" }]
        };
        var client = BuildClient(config, new YieldingSegmentProvider(isMember: true));
        (await client.GetIntAsync("async-int", "user-1", EvaluationContext.Empty)).Should().Be(7);
    }

    [Fact]
    public async Task GetDoubleAsync_async_path_returns_value()
    {
        var config = new ExperimentConfig
        {
            Id            = "async-dbl",
            SubjectType   = "user",
            Variants      = [new VariantConfig { Name = "v1", Allocation = 1.0, Value = 2.71 }],
            GlobalFilters = [new SegmentIncludeFilter { Segment = "seg" }]
        };
        var client = BuildClient(config, new YieldingSegmentProvider(isMember: true));
        (await client.GetDoubleAsync("async-dbl", "user-1", EvaluationContext.Empty)).Should().Be(2.71);
    }

    // ── Edge cases ────────────────────────────────────────────────────────────

    [Fact]
    public async Task SegmentIncludeFilter_with_no_segment_provider_returns_ineligible()
    {
        var config = new ExperimentConfig
        {
            Id            = "seg-no-provider",
            SubjectType   = "user",
            Variants      = [new VariantConfig { Name = "treatment", Allocation = 1.0 }],
            GlobalFilters = [new SegmentIncludeFilter { Segment = "beta" }]
        };
        // No segment provider registered — client built without one.
        var options = new KomentoOptions { Experiments = new HashSet<string> { config.Id } };
        var client  = new ExperimentClient(options, segmentProvider: null);
        await client.UpdateAsync(new Dictionary<string, ExperimentConfig> { [config.Id] = config });

        var result = await client.GetVariantAsync(config.Id, "user-1", EvaluationContext.Empty);
        result.Should().Be(VariantResult.Ineligible);
    }

    [Fact]
    public async Task UpdateAsync_batch_silently_ignores_non_relevant_experiment_ids()
    {
        var client = BuildClient();
        // "other-exp" is not in the relevant set — should be silently dropped.
        var irrelevant = new ExperimentConfig
        {
            Id          = "other-exp",
            SubjectType = "user",
            Variants    = [new VariantConfig { Name = "x", Allocation = 1.0 }]
        };
        await client.UpdateAsync(new Dictionary<string, ExperimentConfig> { [irrelevant.Id] = irrelevant });

        var result = await client.GetVariantAsync("other-exp", "user-1", EvaluationContext.Empty);
        result.Should().Be(VariantResult.NotFound);
    }

    [Fact]
    public async Task UpdateAsync_single_silently_ignores_non_relevant_experiment_id()
    {
        var client = BuildClient();
        var irrelevant = new ExperimentConfig
        {
            Id          = "other-exp",
            SubjectType = "user",
            Variants    = [new VariantConfig { Name = "x", Allocation = 1.0 }]
        };
        await client.UpdateAsync(irrelevant);

        var result = await client.GetVariantAsync("other-exp", "user-1", EvaluationContext.Empty);
        result.Should().Be(VariantResult.NotFound);
    }

    [Fact]
    public async Task SubjectOverride_forces_variant_in_async_evaluation_path()
    {
        // Experiment has both a SegmentIncludeFilter (forces async path) and a SubjectOverride.
        // The override should short-circuit before the segment check.
        var config = new ExperimentConfig
        {
            Id            = "async-override",
            SubjectType   = "user",
            Variants      = [new VariantConfig { Name = "control", Allocation = 1.0 }],
            GlobalFilters = [new SegmentIncludeFilter { Segment = "seg" }],
            Overrides     = [new SubjectOverride { SubjectId = "vip", Variant = "treatment" }]
        };
        var client = BuildClient(config, new YieldingSegmentProvider(isMember: false));
        var result = await client.GetVariantAsync("async-override", "vip", EvaluationContext.Empty);
        (result == "treatment").Should().BeTrue();
    }

    // ── ExperimentExists ──────────────────────────────────────────────────────

    [Fact]
    public void ExperimentExists_returns_false_for_unknown_experiment()
    {
        var client = BuildClient();
        client.ExperimentExists("does-not-exist").Should().BeFalse();
    }

    [Fact]
    public void ExperimentExists_returns_true_for_loaded_experiment()
    {
        var client = BuildClient();
        client.ExperimentExists("exp-1").Should().BeTrue();
    }

    [Fact]
    public async Task ExperimentExists_returns_false_after_removal()
    {
        var client = BuildClient();
        await client.RemoveAsync("exp-1");
        client.ExperimentExists("exp-1").Should().BeFalse();
    }
}

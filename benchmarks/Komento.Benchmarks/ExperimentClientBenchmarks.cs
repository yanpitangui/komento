using BenchmarkDotNet.Attributes;
using Komento;
using Komento.Internals;

/// <summary>
/// Benchmarks for the hot-path assignment engine.
/// The sync path (no segment operations) must show 0 bytes allocated.
/// </summary>
[MemoryDiagnoser]
public class ExperimentClientBenchmarks
{
    private ExperimentClient _client = null!;
    private EvaluationContext _emptyCtx;
    private EvaluationContext _traitCtx;

    private const string SubjectId   = "bench-user-001";
    private const string SyncFlag    = "sync-flag";
    private const string TraitFlag   = "trait-flag";
    private const string SegmentFlag = "segment-flag";

    [GlobalSetup]
    public async Task Setup()
    {
        _client = new ExperimentClient(new KomentoOptions());

        var configs = new Dictionary<string, ExperimentConfig>
        {
            // Simple 50/50 — exercises the sync fast path.
            [SyncFlag] = new ExperimentConfig
            {
                Id          = SyncFlag,
                SubjectType = "user",
                Variants    =
                [
                    new VariantConfig { Name = "control",   Allocation = 0.5 },
                    new VariantConfig { Name = "treatment", Allocation = 0.5 }
                ]
            },

            // Has a TraitEqualsFilter — still sync, but evaluates a filter predicate.
            [TraitFlag] = new ExperimentConfig
            {
                Id            = TraitFlag,
                SubjectType   = "user",
                Variants      = [new VariantConfig { Name = "treatment", Allocation = 1.0 }],
                GlobalFilters = [new TraitEqualsFilter { Key = "platform", Value = "web" }]
            },

            // Has a SegmentIncludeFilter — forces the async code path.
            [SegmentFlag] = new ExperimentConfig
            {
                Id            = SegmentFlag,
                SubjectType   = "user",
                Variants      = [new VariantConfig { Name = "treatment", Allocation = 1.0 }],
                GlobalFilters = [new SegmentIncludeFilter { Segment = "beta-users" }]
            }
        };

        await _client.UpdateAsync(configs);

        _emptyCtx = EvaluationContext.Create().Build();
        _traitCtx = EvaluationContext.Create().Set("platform", "web").Build();
    }

    // ── Sync fast path ────────────────────────────────────────────────────────

    /// <summary>Simple 50/50 split, no filters. Must allocate 0 bytes.</summary>
    [Benchmark(Baseline = true)]
    public ValueTask<VariantResult> GetVariant_NoFilters() =>
        _client.GetVariantAsync(SyncFlag, SubjectId, _emptyCtx);

    /// <summary>TraitEqualsFilter evaluated inline — still sync, must allocate 0 bytes.</summary>
    [Benchmark]
    public ValueTask<VariantResult> GetVariant_TraitFilter() =>
        _client.GetVariantAsync(TraitFlag, SubjectId, _traitCtx);

    /// <summary>Experiment not registered — immediate FrozenDictionary miss. Must allocate 0 bytes.</summary>
    [Benchmark]
    public ValueTask<VariantResult> GetVariant_ExperimentNotFound() =>
        _client.GetVariantAsync("nonexistent", SubjectId, _emptyCtx);

    // ── Async path (segment operations) ──────────────────────────────────────

    /// <summary>SegmentIncludeFilter triggers the async evaluation path.</summary>
    [Benchmark]
    public ValueTask<VariantResult> GetVariant_SegmentFilter() =>
        _client.GetVariantAsync(SegmentFlag, SubjectId, _emptyCtx);

    // ── Typed helpers ─────────────────────────────────────────────────────────

    /// <summary>GetBoolAsync sync unwrap — verifies helper doesn't add overhead.</summary>
    [Benchmark]
    public ValueTask<bool> GetBool_SyncPath() =>
        _client.GetBoolAsync(SyncFlag, SubjectId, _emptyCtx);

    // ── Config hot-swap ───────────────────────────────────────────────────────

    /// <summary>UpdateAsync builds a new FrozenDictionary and atomically swaps it.</summary>
    [Benchmark]
    public async ValueTask UpdateAsync_SingleExperiment()
    {
        var config = new ExperimentConfig
        {
            Id       = SyncFlag,
            SubjectType = "user",
            Variants = [new VariantConfig { Name = "treatment", Allocation = 1.0 }]
        };
        await _client.UpdateAsync(config);
    }
}

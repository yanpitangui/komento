using System.Collections.Frozen;
using System.Threading.Channels;

namespace Komento.Internals;

internal sealed class ExperimentClient : IExperimentClient, IConfigUpdater
{
    private FrozenDictionary<string, CompiledExperiment> _experiments =
        FrozenDictionary<string, CompiledExperiment>.Empty;

    private readonly FrozenSet<string>          _relevantIds;
    private readonly ISegmentProvider?          _segmentProvider;
    private readonly Channel<ExposureEvent>     _exposureChannel;

    public IReadOnlySet<string>       RelevantExperimentIds => _relevantIds;
    public ChannelReader<ExposureEvent> Exposures           => _exposureChannel.Reader;

    public ExperimentClient(KomentoOptions options, ISegmentProvider? segmentProvider = null)
    {
        _relevantIds      = options.Experiments.ToFrozenSet(StringComparer.Ordinal);
        _segmentProvider  = segmentProvider;
        _exposureChannel  = Channel.CreateBounded<ExposureEvent>(new BoundedChannelOptions(options.ExposureChannelCapacity)
        {
            FullMode     = BoundedChannelFullMode.DropWrite,
            SingleWriter = false,
            SingleReader = false
        });
    }

    // ── IExperimentClient ─────────────────────────────────────────────────────

    public ValueTask<VariantResult> GetVariantAsync(
        string flagKey, string subjectId, in EvaluationContext ctx, CancellationToken ct = default)
    {
        var experiments = Volatile.Read(ref _experiments);
        if (!experiments.TryGetValue(flagKey, out var exp))
            return ValueTask.FromResult(VariantResult.NotFound);

        // Fast path: no segment filters or overrides — fully sync, zero allocations
        if (!HasSegmentOperations(exp))
            return ValueTask.FromResult(EvaluateSync(flagKey, subjectId, in ctx, exp));

        // Slow path: segment operations may be truly async (external provider)
        var ctxCopy = ctx;
        return EvaluateAsync(flagKey, subjectId, ctxCopy, exp, ct);
    }

    public ValueTask<bool> GetBoolAsync(
        string flagKey, string subjectId, in EvaluationContext ctx,
        bool defaultValue = default, CancellationToken ct = default)
    {
        var vt = GetVariantAsync(flagKey, subjectId, in ctx, ct);
        if (vt.IsCompletedSuccessfully)
        {
            var r = vt.Result;
            if (!r.IsEligible || r.IsOutsider) return ValueTask.FromResult(defaultValue);
            return r.Value is bool b ? ValueTask.FromResult(b) : ValueTask.FromResult(defaultValue);
        }
        return Await(vt, defaultValue);
        static async ValueTask<bool> Await(ValueTask<VariantResult> t, bool dv)
        {
            var r = await t;
            if (!r.IsEligible || r.IsOutsider) return dv;
            return r.Value is bool b ? b : dv;
        }
    }

    public ValueTask<string> GetStringAsync(
        string flagKey, string subjectId, in EvaluationContext ctx,
        string defaultValue = "", CancellationToken ct = default)
    {
        var vt = GetVariantAsync(flagKey, subjectId, in ctx, ct);
        if (vt.IsCompletedSuccessfully)
        {
            var r = vt.Result;
            if (!r.IsEligible || r.IsOutsider) return ValueTask.FromResult(defaultValue);
            return r.Value is string s ? ValueTask.FromResult(s) : ValueTask.FromResult(defaultValue);
        }
        return Await(vt, defaultValue);
        static async ValueTask<string> Await(ValueTask<VariantResult> t, string dv)
        {
            var r = await t;
            if (!r.IsEligible || r.IsOutsider) return dv;
            return r.Value is string s ? s : dv;
        }
    }

    public ValueTask<int> GetIntAsync(
        string flagKey, string subjectId, in EvaluationContext ctx,
        int defaultValue = default, CancellationToken ct = default)
    {
        var vt = GetVariantAsync(flagKey, subjectId, in ctx, ct);
        if (vt.IsCompletedSuccessfully)
        {
            var r = vt.Result;
            if (!r.IsEligible || r.IsOutsider) return ValueTask.FromResult(defaultValue);
            return r.Value is int n ? ValueTask.FromResult(n) : ValueTask.FromResult(defaultValue);
        }
        return Await(vt, defaultValue);
        static async ValueTask<int> Await(ValueTask<VariantResult> t, int dv)
        {
            var r = await t;
            if (!r.IsEligible || r.IsOutsider) return dv;
            return r.Value is int n ? n : dv;
        }
    }

    public ValueTask<double> GetDoubleAsync(
        string flagKey, string subjectId, in EvaluationContext ctx,
        double defaultValue = default, CancellationToken ct = default)
    {
        var vt = GetVariantAsync(flagKey, subjectId, in ctx, ct);
        if (vt.IsCompletedSuccessfully)
        {
            var r = vt.Result;
            if (!r.IsEligible || r.IsOutsider) return ValueTask.FromResult(defaultValue);
            return r.Value is double d ? ValueTask.FromResult(d) : ValueTask.FromResult(defaultValue);
        }
        return Await(vt, defaultValue);
        static async ValueTask<double> Await(ValueTask<VariantResult> t, double dv)
        {
            var r = await t;
            if (!r.IsEligible || r.IsOutsider) return dv;
            return r.Value is double d ? d : dv;
        }
    }

    // ── Evaluation paths ──────────────────────────────────────────────────────

    private VariantResult EvaluateSync(
        string flagKey, string subjectId, in EvaluationContext ctx, CompiledExperiment exp)
    {
        // 1. Subject overrides
        var overrides = exp.Overrides;
        for (var i = 0; i < overrides.Length; i++)
        {
            if (overrides[i] is SubjectOverride so &&
                string.Equals(so.SubjectId, subjectId, StringComparison.Ordinal))
            {
                var r = MakeResult(so.Variant, exp, isEligible: true, isOutsider: false);
                FireExposure(flagKey, subjectId, r);
                return r;
            }
        }

        // 2. Global filters (only TraitEqualsFilter reaches here — HasSegmentOperations guards)
        var filters = exp.Filters;
        for (var i = 0; i < filters.Length; i++)
        {
            if (filters[i] is TraitEqualsFilter tf &&
                !(ctx.TryGetValue(tf.Key, out var val) &&
                  string.Equals(val?.ToString(), tf.Value, StringComparison.Ordinal)))
            {
                FireExposure(flagKey, subjectId, VariantResult.Ineligible);
                return VariantResult.Ineligible;
            }
        }

        // 3. Bucket assignment
        return AssignBucket(flagKey, subjectId, exp);
    }

    private async ValueTask<VariantResult> EvaluateAsync(
        string flagKey, string subjectId, EvaluationContext ctx, CompiledExperiment exp, CancellationToken ct)
    {
        // 1. Subject overrides
        var overrides = exp.Overrides;
        for (var i = 0; i < overrides.Length; i++)
        {
            if (overrides[i] is SubjectOverride so &&
                string.Equals(so.SubjectId, subjectId, StringComparison.Ordinal))
            {
                var r = MakeResult(so.Variant, exp, isEligible: true, isOutsider: false);
                FireExposure(flagKey, subjectId, r);
                return r;
            }
        }

        // 2. Global filters
        var filters = exp.Filters;
        for (var i = 0; i < filters.Length; i++)
        {
            switch (filters[i])
            {
                case TraitEqualsFilter tf:
                    if (!(ctx.TryGetValue(tf.Key, out var val) &&
                          string.Equals(val?.ToString(), tf.Value, StringComparison.Ordinal)))
                    {
                        FireExposure(flagKey, subjectId, VariantResult.Ineligible);
                        return VariantResult.Ineligible;
                    }
                    break;

                case SegmentIncludeFilter sf:
                    if (_segmentProvider is null ||
                        !await _segmentProvider.IsInSegmentAsync(subjectId, sf.Segment, ct))
                    {
                        FireExposure(flagKey, subjectId, VariantResult.Ineligible);
                        return VariantResult.Ineligible;
                    }
                    break;
            }
        }

        // 3. Segment overrides
        for (var i = 0; i < overrides.Length; i++)
        {
            if (overrides[i] is SegmentOverride segOvr &&
                _segmentProvider is not null &&
                await _segmentProvider.IsInSegmentAsync(subjectId, segOvr.Segment, ct))
            {
                var r = MakeResult(segOvr.Variant, exp, isEligible: true, isOutsider: false);
                FireExposure(flagKey, subjectId, r);
                return r;
            }
        }

        // 4. Bucket assignment
        return AssignBucket(flagKey, subjectId, exp);
    }

    private VariantResult AssignBucket(string flagKey, string subjectId, CompiledExperiment exp)
    {
        var bucket   = Hasher.ComputeBucket(flagKey, subjectId);
        var variants = exp.Variants;
        for (var i = 0; i < variants.Length; i++)
        {
            var ranges = variants[i].Ranges;
            for (var j = 0; j < ranges.Length; j++)
            {
                if (ranges[j].Contains(bucket))
                {
                    var r = new VariantResult
                    {
                        VariantName = variants[i].Name,
                        Value       = variants[i].Value,
                        IsEligible  = true,
                    };
                    FireExposure(flagKey, subjectId, r);
                    return r;
                }
            }
        }

        var outsider = VariantResult.Outsider();
        FireExposure(flagKey, subjectId, outsider);
        return outsider;
    }

    // ── IConfigUpdater ────────────────────────────────────────────────────────

    public ValueTask UpdateAsync(
        IReadOnlyDictionary<string, ExperimentConfig> configs, CancellationToken ct = default)
    {
        var current  = Volatile.Read(ref _experiments);
        var compiled = new Dictionary<string, CompiledExperiment>(current, StringComparer.Ordinal);

        foreach (var (id, cfg) in configs)
            if (_relevantIds.Contains(id))
                compiled[id] = ConfigCompiler.Compile(cfg);

        Volatile.Write(ref _experiments, compiled.ToFrozenDictionary(StringComparer.Ordinal));
        return ValueTask.CompletedTask;
    }

    public ValueTask UpdateAsync(ExperimentConfig config, CancellationToken ct = default)
    {
        if (!_relevantIds.Contains(config.Id))
            return ValueTask.CompletedTask;

        var current  = Volatile.Read(ref _experiments);
        var compiled = new Dictionary<string, CompiledExperiment>(current, StringComparer.Ordinal)
        {
            [config.Id] = ConfigCompiler.Compile(config)
        };
        Volatile.Write(ref _experiments, compiled.ToFrozenDictionary(StringComparer.Ordinal));
        return ValueTask.CompletedTask;
    }

    public ValueTask RemoveAsync(string experimentId, CancellationToken ct = default)
    {
        var current  = Volatile.Read(ref _experiments);
        var compiled = new Dictionary<string, CompiledExperiment>(current, StringComparer.Ordinal);
        compiled.Remove(experimentId);
        Volatile.Write(ref _experiments, compiled.ToFrozenDictionary(StringComparer.Ordinal));
        return ValueTask.CompletedTask;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static bool HasSegmentOperations(CompiledExperiment exp)
    {
        for (var i = 0; i < exp.Filters.Length; i++)
            if (exp.Filters[i] is SegmentIncludeFilter) return true;
        for (var i = 0; i < exp.Overrides.Length; i++)
            if (exp.Overrides[i] is SegmentOverride) return true;
        return false;
    }

    private static VariantResult MakeResult(
        string variantName, CompiledExperiment exp, bool isEligible, bool isOutsider)
    {
        object? value = null;
        for (var i = 0; i < exp.Variants.Length; i++)
            if (string.Equals(exp.Variants[i].Name, variantName, StringComparison.Ordinal))
            { value = exp.Variants[i].Value; break; }

        return new VariantResult
        {
            VariantName = variantName,
            Value       = value,
            IsEligible  = isEligible,
            IsOutsider  = isOutsider
        };
    }

    private void FireExposure(string flagKey, string subjectId, VariantResult result)
    {
        _exposureChannel.Writer.TryWrite(new ExposureEvent
        {
            FlagKey     = flagKey,
            SubjectId   = subjectId,
            VariantName = result.VariantName,
            IsEligible  = result.IsEligible,
            IsOutsider  = result.IsOutsider,
            Timestamp   = DateTimeOffset.UtcNow
        });
    }
}

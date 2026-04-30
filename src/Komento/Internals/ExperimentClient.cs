using System.Collections.Frozen;

namespace Komento.Internals;

internal sealed class ExperimentClient : IExperimentClient, IConfigUpdater
{
    private FrozenDictionary<string, CompiledExperiment> _experiments =
        FrozenDictionary<string, CompiledExperiment>.Empty;

    private readonly FrozenSet<string>               _relevantIds;
    private readonly ISegmentProvider?               _segmentProvider;
    private readonly Func<ExposureEvent, ValueTask>? _onExposure;

    public IReadOnlySet<string> RelevantExperimentIds => _relevantIds;

    public ExperimentClient(KomentoOptions options, ISegmentProvider? segmentProvider = null)
    {
        _relevantIds     = options.Experiments.ToFrozenSet(StringComparer.Ordinal);
        _segmentProvider = segmentProvider;
        _onExposure      = options.OnExposure;
    }

    // ── IExperimentClient ─────────────────────────────────────────────────────
    // async can't accept in-params; capture ctx by value before async core.

    public ValueTask<VariantResult> GetVariantAsync(
        string flagKey, string subjectId, in EvaluationContext ctx, CancellationToken ct = default)
    {
        var ctxCopy = ctx;
        return GetVariantAsyncCore(flagKey, subjectId, ctxCopy, ct);
    }

    public ValueTask<bool> GetBoolAsync(
        string flagKey, string subjectId, in EvaluationContext ctx,
        bool defaultValue = default, CancellationToken ct = default)
    {
        var ctxCopy = ctx;
        return GetBoolAsyncCore(flagKey, subjectId, ctxCopy, defaultValue, ct);
    }

    public ValueTask<string> GetStringAsync(
        string flagKey, string subjectId, in EvaluationContext ctx,
        string defaultValue = "", CancellationToken ct = default)
    {
        var ctxCopy = ctx;
        return GetStringAsyncCore(flagKey, subjectId, ctxCopy, defaultValue, ct);
    }

    public ValueTask<int> GetIntAsync(
        string flagKey, string subjectId, in EvaluationContext ctx,
        int defaultValue = default, CancellationToken ct = default)
    {
        var ctxCopy = ctx;
        return GetIntAsyncCore(flagKey, subjectId, ctxCopy, defaultValue, ct);
    }

    public ValueTask<double> GetDoubleAsync(
        string flagKey, string subjectId, in EvaluationContext ctx,
        double defaultValue = default, CancellationToken ct = default)
    {
        var ctxCopy = ctx;
        return GetDoubleAsyncCore(flagKey, subjectId, ctxCopy, defaultValue, ct);
    }

    // ── async cores ───────────────────────────────────────────────────────────

    private async ValueTask<VariantResult> GetVariantAsyncCore(
        string flagKey, string subjectId, EvaluationContext ctx, CancellationToken ct)
    {
        var experiments = Volatile.Read(ref _experiments);

        if (!experiments.TryGetValue(flagKey, out var exp))
            return VariantResult.NotFound;

        // 1. Subject overrides (sync scan — no LINQ)
        var overrides = exp.Overrides;
        for (var i = 0; i < overrides.Length; i++)
        {
            if (overrides[i] is SubjectOverride so &&
                string.Equals(so.SubjectId, subjectId, StringComparison.Ordinal))
            {
                var r = MakeResult(so.Variant, exp, isEligible: true, isOutsider: false);
                await FireExposure(flagKey, subjectId, r, ct);
                return r;
            }
        }

        // 2. Global filters
        var filters = exp.Filters;
        for (var i = 0; i < filters.Length; i++)
        {
            if (!await EvaluateFilter(filters[i], subjectId, ctx, ct))
            {
                var r = VariantResult.Ineligible;
                await FireExposure(flagKey, subjectId, r, ct);
                return r;
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
                await FireExposure(flagKey, subjectId, r, ct);
                return r;
            }
        }

        // 4. Hash-bucket assignment
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
                        IsOutsider  = false
                    };
                    await FireExposure(flagKey, subjectId, r, ct);
                    return r;
                }
            }
        }

        // 5. Outsider — eligible but not in any bucket
        var outsider = VariantResult.Outsider();
        await FireExposure(flagKey, subjectId, outsider, ct);
        return outsider;
    }

    private async ValueTask<bool> GetBoolAsyncCore(
        string flagKey, string subjectId, EvaluationContext ctx, bool defaultValue, CancellationToken ct)
    {
        var r = await GetVariantAsyncCore(flagKey, subjectId, ctx, ct);
        if (!r.IsEligible || r.IsOutsider) return defaultValue;
        return r.Value is bool b ? b : defaultValue;
    }

    private async ValueTask<string> GetStringAsyncCore(
        string flagKey, string subjectId, EvaluationContext ctx, string defaultValue, CancellationToken ct)
    {
        var r = await GetVariantAsyncCore(flagKey, subjectId, ctx, ct);
        if (!r.IsEligible || r.IsOutsider) return defaultValue;
        return r.Value is string s ? s : defaultValue;
    }

    private async ValueTask<int> GetIntAsyncCore(
        string flagKey, string subjectId, EvaluationContext ctx, int defaultValue, CancellationToken ct)
    {
        var r = await GetVariantAsyncCore(flagKey, subjectId, ctx, ct);
        if (!r.IsEligible || r.IsOutsider) return defaultValue;
        return r.Value is int n ? n : defaultValue;
    }

    private async ValueTask<double> GetDoubleAsyncCore(
        string flagKey, string subjectId, EvaluationContext ctx, double defaultValue, CancellationToken ct)
    {
        var r = await GetVariantAsyncCore(flagKey, subjectId, ctx, ct);
        if (!r.IsEligible || r.IsOutsider) return defaultValue;
        return r.Value is double d ? d : defaultValue;
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

    private async ValueTask<bool> EvaluateFilter(
        FilterConfig filter, string subjectId, EvaluationContext ctx, CancellationToken ct)
    {
        return filter switch
        {
            TraitEqualsFilter f =>
                ctx.TryGetValue(f.Key, out var val) &&
                string.Equals(val?.ToString(), f.Value, StringComparison.Ordinal),

            SegmentIncludeFilter f =>
                _segmentProvider is not null &&
                await _segmentProvider.IsInSegmentAsync(subjectId, f.Segment, ct),

            _ => true
        };
    }

    private ValueTask FireExposure(
        string flagKey, string subjectId, VariantResult result, CancellationToken ct)
    {
        if (_onExposure is null) return ValueTask.CompletedTask;
        return _onExposure(new ExposureEvent
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

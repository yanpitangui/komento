using System.Collections.Frozen;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Threading.Channels;

namespace Komento.Internals;

internal sealed class ExperimentClient
    : IExperimentClient, IConfigUpdater, IConfigChanges, IExposureStream, IExperimentTracker, ITrackStream
{
    private FrozenDictionary<string, CompiledExperiment> _experiments =
        FrozenDictionary<string, CompiledExperiment>.Empty;

    private readonly ISegmentProvider?          _segmentProvider;
    private readonly EvaluationContext          _staticContext;
    private readonly TimeProvider               _timeProvider;
    private readonly bool                       _emitActivityEvents;
    private readonly bool                       _includeSubjectIdInActivityEvents;
    private readonly Channel<ExposureEvent>?    _exposureChannel;
    private readonly Channel<TrackEvent>?       _trackChannel;
    private readonly bool                       _includeContextInTrackEvents;

    public ChannelReader<ExposureEvent> Reader => _exposureChannel?.Reader
        ?? throw new InvalidOperationException(
            $"Exposure stream is disabled. Set {nameof(KomentoOptions)}.{nameof(KomentoOptions.EnableExposureStream)} = true.");

    ChannelReader<TrackEvent> ITrackStream.Reader => _trackChannel?.Reader
        ?? throw new InvalidOperationException(
            $"Track stream is disabled. Set {nameof(KomentoOptions)}.{nameof(KomentoOptions.EnableTrackStream)} = true.");

    public ExperimentClient(
        KomentoOptions    options,
        ISegmentProvider? segmentProvider = null,
        TimeProvider?     timeProvider    = null)
    {
        _segmentProvider                  = segmentProvider;
        _staticContext                    = options.StaticContext;
        _timeProvider                     = timeProvider ?? TimeProvider.System;
        _emitActivityEvents               = options.EmitActivityEvents;
        _includeSubjectIdInActivityEvents = options.IncludeSubjectIdInActivityEvents;
        _includeContextInTrackEvents      = options.IncludeContextInTrackEvents;
        if (options.EnableTrackStream)
            _trackChannel = Channel.CreateBounded<TrackEvent>(new BoundedChannelOptions(options.TrackChannelCapacity)
            {
                // Wait: TryWrite returns false when the channel is full, so drops can be counted.
                FullMode     = BoundedChannelFullMode.Wait,
                SingleWriter = false,
                SingleReader = false
            });
        if (options.EnableExposureStream)
            _exposureChannel = Channel.CreateBounded<ExposureEvent>(new BoundedChannelOptions(options.ExposureChannelCapacity)
            {
                // Wait: TryWrite returns false when the channel is full, so drops can be counted.
                FullMode     = BoundedChannelFullMode.Wait,
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

    public bool ExperimentExists(string flagKey)
        => Volatile.Read(ref _experiments).ContainsKey(flagKey);

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
                var r = MakeResult(so.Variant, exp, AssignmentSource.SubjectOverride);
                FireExposure(flagKey, subjectId, exp, r);
                return r;
            }
        }

        // 2. Global filters (only TraitEqualsFilter reaches here — HasSegmentOperations guards)
        var filters = exp.Filters;
        for (var i = 0; i < filters.Length; i++)
        {
            if (filters[i] is TraitEqualsFilter tf &&
                !(TryGetAttribute(in ctx, tf.Key, out var val) &&
                  string.Equals(val?.ToString(), tf.Value, StringComparison.Ordinal)))
            {
                var ineligible = VariantResult.Ineligible with { SubjectType = exp.SubjectType };
                FireExposure(flagKey, subjectId, exp, ineligible);
                return ineligible;
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
                var r = MakeResult(so.Variant, exp, AssignmentSource.SubjectOverride);
                FireExposure(flagKey, subjectId, exp, r);
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
                    if (!(TryGetAttribute(in ctx, tf.Key, out var val) &&
                          string.Equals(val?.ToString(), tf.Value, StringComparison.Ordinal)))
                    {
                        var ineligible = VariantResult.Ineligible with { SubjectType = exp.SubjectType };
                        FireExposure(flagKey, subjectId, exp, ineligible);
                        return ineligible;
                    }
                    break;

                case SegmentIncludeFilter sf:
                    if (_segmentProvider is null ||
                        !await _segmentProvider.IsInSegmentAsync(subjectId, sf.Segment, ct))
                    {
                        var ineligible = VariantResult.Ineligible with { SubjectType = exp.SubjectType };
                        FireExposure(flagKey, subjectId, exp, ineligible);
                        return ineligible;
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
                var r = MakeResult(segOvr.Variant, exp, AssignmentSource.SegmentOverride);
                FireExposure(flagKey, subjectId, exp, r);
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
                        Source      = AssignmentSource.Hash,
                        SubjectType = exp.SubjectType
                    };
                    FireExposure(flagKey, subjectId, exp, r);
                    return r;
                }
            }
        }

        var outsider = VariantResult.Outsider() with { SubjectType = exp.SubjectType };
        FireExposure(flagKey, subjectId, exp, outsider);
        return outsider;
    }

    // ── IConfigUpdater ────────────────────────────────────────────────────────

    public event Action<IReadOnlyCollection<string>>? Changed;

    private void NotifyChanged(List<string> changed)
    {
        if (changed.Count == 0 || Changed is not { } handlers) return;

        foreach (var handler in handlers.GetInvocationList().Cast<Action<IReadOnlyCollection<string>>>())
        {
            try { handler(changed); }
            catch { /* a subscriber must not break config updates */ }
        }
    }

    public ValueTask UpdateAsync(
        IReadOnlyDictionary<string, ExperimentConfig> configs, IReadOnlySet<string> experimentIds, CancellationToken ct = default)
    {
        var current  = Volatile.Read(ref _experiments);
        var compiled = new Dictionary<string, CompiledExperiment>(current, StringComparer.Ordinal);
        var changed  = new List<string>();

        var loadAll = experimentIds.Count == 0;
        foreach (var (id, cfg) in configs)
        {
            if (!loadAll && !experimentIds.Contains(id)) continue;

            if (current.TryGetValue(id, out var existing) && ExperimentConfigComparer.AreEqual(existing.Config, cfg))
                continue;   // unchanged: keep the compiled experiment we already have

            compiled[id] = ConfigCompiler.Compile(cfg);
            changed.Add(id);
        }

        Volatile.Write(ref _experiments, compiled.ToFrozenDictionary(StringComparer.Ordinal));
        NotifyChanged(changed);
        return ValueTask.CompletedTask;
    }

    public ValueTask UpdateAsync(IReadOnlyDictionary<string, ExperimentConfig> configs, CancellationToken ct = default)
        => UpdateAsync(configs, new HashSet<string>(), ct);

    public ValueTask UpdateAsync(ExperimentConfig config, CancellationToken ct = default)
        => UpdateAsync(new Dictionary<string, ExperimentConfig> { [config.Id] = config }, new HashSet<string>(), ct);

    public ValueTask RemoveAsync(string experimentId, CancellationToken ct = default)
    {
        var current = Volatile.Read(ref _experiments);
        if (!current.ContainsKey(experimentId))
            return ValueTask.CompletedTask;

        var compiled = new Dictionary<string, CompiledExperiment>(current, StringComparer.Ordinal);
        compiled.Remove(experimentId);
        Volatile.Write(ref _experiments, compiled.ToFrozenDictionary(StringComparer.Ordinal));
        NotifyChanged([experimentId]);
        return ValueTask.CompletedTask;
    }

    // ── IExperimentTracker ────────────────────────────────────────────────────

    public void Track(
        string eventName, string subjectId, double? value = null,
        IReadOnlyDictionary<string, object?>? properties = null)
        => Track(eventName, subjectId, in EvaluationContext.Empty, value, properties);

    public void Track(
        string eventName, string subjectId, in EvaluationContext ctx,
        double? value = null, IReadOnlyDictionary<string, object?>? properties = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(eventName);
        ArgumentException.ThrowIfNullOrEmpty(subjectId);
        if (_trackChannel is null) return;

        var evt = new TrackEvent
        {
            EventName  = eventName,
            SubjectId  = subjectId,
            Value      = value,
            Properties = SnapshotProperties(properties),
            Context    = _includeContextInTrackEvents ? MergeContext(in ctx) : null,
            Timestamp  = _timeProvider.GetUtcNow()
        };

        if (!_trackChannel.Writer.TryWrite(evt))
            KomentoMetrics.TrackDropped.Add(1, new KeyValuePair<string, object?>("reason", "queue_full"));
    }

    private static IReadOnlyDictionary<string, object?>? SnapshotProperties(
        IReadOnlyDictionary<string, object?>? properties)
        => properties is null || properties.Count == 0
            ? null
            : properties.ToFrozenDictionary(StringComparer.Ordinal);

    // Static context first, then the per-call context on top, so per-call attributes win.
    private IReadOnlyDictionary<string, object>? MergeContext(in EvaluationContext ctx)
    {
        var merged = new Dictionary<string, object>(StringComparer.Ordinal);
        _staticContext.CopyTo(merged);
        ctx.CopyTo(merged);
        return merged.Count == 0 ? null : merged.ToFrozenDictionary(StringComparer.Ordinal);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Looks an attribute up in the per-call context first, then in <see cref="KomentoOptions.StaticContext"/>,
    /// so per-call values take precedence. A lookup in each (no merged copy) keeps evaluation allocation-free.
    /// </summary>
    private bool TryGetAttribute(in EvaluationContext ctx, string key, [NotNullWhen(true)] out object? value)
        => ctx.TryGetValue(key, out value) || _staticContext.TryGetValue(key, out value);

    private static bool HasSegmentOperations(CompiledExperiment exp)
    {
        for (var i = 0; i < exp.Filters.Length; i++)
            if (exp.Filters[i] is SegmentIncludeFilter) return true;
        for (var i = 0; i < exp.Overrides.Length; i++)
            if (exp.Overrides[i] is SegmentOverride) return true;
        return false;
    }

    private static VariantResult MakeResult(
        string variantName, CompiledExperiment exp, AssignmentSource source)
    {
        object? value = null;
        for (var i = 0; i < exp.Variants.Length; i++)
            if (string.Equals(exp.Variants[i].Name, variantName, StringComparison.Ordinal))
            { value = exp.Variants[i].Value; break; }

        return new VariantResult
        {
            VariantName = variantName,
            Value       = value,
            IsEligible  = true,
            IsOutsider  = false,
            Source      = source,
            SubjectType = exp.SubjectType
        };
    }

    // OpenTelemetry feature-flag convention: https://opentelemetry.io/docs/specs/semconv/feature-flags/feature-flags-events/
    private void AddActivityEvent(Activity activity, string flagKey, string subjectId, CompiledExperiment exp, VariantResult result)
    {
        var outcome = OutcomeOf(result);
        var tags    = new ActivityTagsCollection
        {
            ["feature_flag.key"]            = flagKey,
            ["feature_flag.result.variant"] = result.VariantName,
            ["feature_flag.result.reason"]  = outcome == "assigned" ? "split" : "default",
            ["feature_flag.provider.name"]  = "Komento",
            ["komento.outcome"]             = outcome,
            ["komento.subject_type"]        = exp.SubjectType
        };
        if (_includeSubjectIdInActivityEvents)
            tags["feature_flag.context.id"] = subjectId;

        activity.AddEvent(new ActivityEvent("feature_flag.evaluation", tags: tags));
    }

    private static string OutcomeOf(VariantResult result)
        => result.IsOutsider ? "outsider"
         : result.IsEligible ? "assigned"
         : "ineligible";

    private void FireExposure(string flagKey, string subjectId, CompiledExperiment exp, VariantResult result)
    {
        KomentoMetrics.Exposures.Add(1,
            new KeyValuePair<string, object?>("experiment", flagKey),
            new KeyValuePair<string, object?>("variant", result.VariantName),
            new KeyValuePair<string, object?>("outcome", OutcomeOf(result)));

        if (_emitActivityEvents && Activity.Current is { IsAllDataRequested: true } activity)
            AddActivityEvent(activity, flagKey, subjectId, exp, result);

        if (_exposureChannel is null) return;

        var written = _exposureChannel.Writer.TryWrite(new ExposureEvent
        {
            FlagKey     = flagKey,
            SubjectId   = subjectId,
            SubjectType = exp.SubjectType,
            VariantName = result.VariantName,
            IsEligible  = result.IsEligible,
            IsOutsider  = result.IsOutsider,
            Timestamp   = _timeProvider.GetUtcNow()
        });

        if (!written)
            KomentoMetrics.ExposuresDropped.Add(1, new KeyValuePair<string, object?>("experiment", flagKey));
    }
}

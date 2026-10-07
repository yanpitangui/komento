using System.Text.Json;
using OpenFeature;
using OpenFeature.Constant;
using OpenFeature.Model;
using OFContext = OpenFeature.Model.EvaluationContext;
using KomentoCtx = Komento.EvaluationContext;

namespace Komento.OpenFeature;

public sealed class KomentoFeatureProvider(
    IExperimentClient   client,
    IExperimentTracker? tracker = null,
    IExperimentSource?  source  = null,
    IConfigUpdater?     updater = null,
    IConfigChanges?     changes = null) : FeatureProvider
{
    public override Metadata GetMetadata() => new("Komento");

    /// <summary>
    /// Loads the configs from the registered <see cref="IExperimentSource"/> (the same load as
    /// <c>InitializeKomentoAsync</c>), so registering the provider is enough to start evaluating. When no source
    /// or updater is registered the provider is ready immediately. A failing source makes the SDK report an
    /// error status.
    /// </summary>
    public override async Task InitializeAsync(OFContext context, CancellationToken cancellationToken = default)
    {
        if (source is not null && updater is not null)
        {
            var all     = new HashSet<string>();
            var configs = await source.LoadAsync(all, cancellationToken).ConfigureAwait(false);
            await updater.UpdateAsync(configs, all, cancellationToken).ConfigureAwait(false);
        }

        // Subscribe after the initial load, so loading the configs does not announce itself as a change.
        if (changes is not null && _onChanged is null)
        {
            _onChanged = ids => EventChannel.Writer.TryWrite(new ProviderEventPayload
            {
                Type         = ProviderEventTypes.ProviderConfigurationChanged,
                ProviderName = "Komento",
                FlagsChanged = [.. ids]
            });
            changes.Changed += _onChanged;
        }
    }

    /// <summary>Stops announcing config changes.</summary>
    public override Task ShutdownAsync(CancellationToken cancellationToken = default)
    {
        if (changes is not null && _onChanged is not null)
        {
            changes.Changed -= _onChanged;
            _onChanged = null;
        }
        return Task.CompletedTask;
    }

    private Action<IReadOnlyCollection<string>>? _onChanged;

    /// <summary>
    /// Forwards OpenFeature tracking calls to <see cref="IExperimentTracker"/>. Does nothing when no tracker is
    /// registered or when <see cref="KomentoOptions.EnableTrackStream"/> is off.
    /// </summary>
    public override void Track(
        string trackingEventName, OFContext? evaluationContext = null, TrackingEventDetails? trackingEventDetails = null)
    {
        if (tracker is null) return;

        if (string.IsNullOrEmpty(trackingEventName))
        {
            CountDropped("no_event_name");
            return;
        }

        if (string.IsNullOrEmpty(evaluationContext?.TargetingKey))
        {
            CountDropped("no_subject");
            return;
        }

        // The SDK keeps the numeric value apart from the other fields.
        var value = trackingEventDetails?.Value;

        Dictionary<string, object?>? properties = null;
        if (trackingEventDetails is not null)
        {
            foreach (var field in trackingEventDetails.AsDictionary())
            {
                var raw = ValueToObject(field.Value);
                if (raw is not null)
                    (properties ??= new Dictionary<string, object?>(StringComparer.Ordinal))[field.Key] = raw;
            }
        }

        var komentoCtx = MapContext(evaluationContext);
        tracker.Track(trackingEventName, evaluationContext.TargetingKey, in komentoCtx, value, properties);
    }

    private static void CountDropped(string reason)
        => OpenFeatureMetrics.TrackDropped.Add(1, new KeyValuePair<string, object?>("reason", reason));

    public override Task<ResolutionDetails<bool>> ResolveBooleanValueAsync(
        string flagKey, bool defaultValue, OFContext? context = null, CancellationToken cancellationToken = default)
        => ResolveAsync(flagKey, defaultValue, context, cancellationToken,
            static r => r.Value is bool b ? (true, b) : (false, default));

    public override Task<ResolutionDetails<string>> ResolveStringValueAsync(
        string flagKey, string defaultValue, OFContext? context = null, CancellationToken cancellationToken = default)
        => ResolveAsync(flagKey, defaultValue, context, cancellationToken,
            static r => r.Value is string s ? (true, s) : (false, string.Empty));

    public override Task<ResolutionDetails<int>> ResolveIntegerValueAsync(
        string flagKey, int defaultValue, OFContext? context = null, CancellationToken cancellationToken = default)
        => ResolveAsync(flagKey, defaultValue, context, cancellationToken,
            static r => r.Value is int i ? (true, i) : (false, default));

    public override Task<ResolutionDetails<double>> ResolveDoubleValueAsync(
        string flagKey, double defaultValue, OFContext? context = null, CancellationToken cancellationToken = default)
        => ResolveAsync(flagKey, defaultValue, context, cancellationToken,
            static r => r.Value is double d ? (true, d) : (false, default));

    public override Task<ResolutionDetails<Value>> ResolveStructureValueAsync(
        string flagKey, Value defaultValue, OFContext? context = null, CancellationToken cancellationToken = default)
        => ResolveAsync(flagKey, defaultValue, context, cancellationToken,
            static r =>
            {
                var v = ConvertToValue(r.Value);
                return v is not null ? (true, v) : (false, new Value());
            });

    private async Task<ResolutionDetails<T>> ResolveAsync<T>(
        string flagKey, T defaultValue, OFContext? context, CancellationToken ct,
        Func<VariantResult, (bool ok, T value)> tryExtract)
    {
        if (string.IsNullOrEmpty(context?.TargetingKey))
            return new ResolutionDetails<T>(flagKey, defaultValue,
                errorType: ErrorType.TargetingKeyMissing, reason: Reason.Error);

        if (!client.ExperimentExists(flagKey))
            return new ResolutionDetails<T>(flagKey, defaultValue,
                errorType: ErrorType.FlagNotFound, reason: Reason.Default);

        var komentoCtx = MapContext(context);
        var result = await client.GetVariantAsync(flagKey, context.TargetingKey, in komentoCtx, ct)
            .ConfigureAwait(false);

        if (!result.IsEligible || result.IsOutsider)
            return new ResolutionDetails<T>(flagKey, defaultValue,
                reason: Reason.Default, variant: result.VariantName);

        var (ok, value) = tryExtract(result);
        return ok
            ? new ResolutionDetails<T>(flagKey, value,
                reason: Reason.TargetingMatch, variant: result.VariantName)
            : new ResolutionDetails<T>(flagKey, defaultValue,
                errorType: ErrorType.ParseError, reason: Reason.Error, variant: result.VariantName);
    }

    private static KomentoCtx MapContext(OFContext context)
    {
        var dict = context.AsDictionary();
        if (dict.Count == 0) return KomentoCtx.Empty;

        var builder = KomentoCtx.Create();
        foreach (var kvp in dict)
        {
            if (string.Equals(kvp.Key, "targetingKey", StringComparison.Ordinal)) continue;
            var raw = ValueToObject(kvp.Value);
            if (raw is not null) builder.Set(kvp.Key, raw);
        }
        return builder.Build();
    }

    private static object? ValueToObject(Value v)
    {
        if (v.IsBoolean) return v.AsBoolean;
        if (v.IsString)  return v.AsString;
        if (v.IsNumber)  return v.AsDouble;
        return null;
    }

    private static Value? ConvertToValue(object? raw)
    {
        if (raw is null)     return null;
        if (raw is bool b)   return new Value(b);
        if (raw is string s) return new Value(s);
        if (raw is int i)    return new Value(i);
        if (raw is double d) return new Value(d);
        try
        {
            var json = JsonSerializer.Serialize(raw);
            var element = JsonSerializer.Deserialize<JsonElement>(json);
            return JsonElementToValue(element);
        }
        catch
        {
            return null;
        }
    }

    private static Value JsonElementToValue(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.True   => new Value(true),
        JsonValueKind.False  => new Value(false),
        JsonValueKind.String => new Value(element.GetString()!),
        JsonValueKind.Number => new Value(element.GetDouble()),
        JsonValueKind.Array  => new Value(JsonArrayToList(element)),
        JsonValueKind.Object => new Value(JsonObjectToStructure(element)),
        _                    => new Value()
    };

    private static List<Value> JsonArrayToList(JsonElement element)
    {
        var list = new List<Value>(element.GetArrayLength());
        foreach (var item in element.EnumerateArray())
            list.Add(JsonElementToValue(item));
        return list;
    }

    private static Structure JsonObjectToStructure(JsonElement element)
    {
        var builder = Structure.Builder();
        foreach (var prop in element.EnumerateObject())
            builder.Set(prop.Name, JsonElementToValue(prop.Value));
        return builder.Build();
    }
}

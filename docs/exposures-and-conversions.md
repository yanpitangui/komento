# Exposures and conversions

An experiment is judged by two kinds of data: who was assigned which variant (*exposures*), and what they did afterwards (*conversions*). Komento produces both and hands them to you. It does not analyze results; send the data to your warehouse, events pipeline or product-analytics tool.

| | Metrics | Streams and sinks |
|---|---|---|
| For | Dashboards and alerts: "how many exposures per variant?" | Analysis: "which subject saw which variant, and when?" |
| Needs | Nothing (always on) | `EnableExposureStream` / `EnableTrackStream`, plus `Komento.Sinks` to deliver |
| Contains subject IDs | No | Yes |

Metrics and traces are covered in [Metrics and traces](metrics-and-traces.md). Delivery to a destination is covered in [Sinks](sinks.md).

## Exposures

An exposure is recorded every time Komento evaluates an experiment for a subject. Enable the stream (off by default; with it off nothing is buffered):

```csharp
services.AddKomento(o => o.EnableExposureStream = true);
```

### The exposure event

```csharp
public readonly struct ExposureEvent
{
    public string?        FlagKey     { get; init; }   // the experiment id
    public string?        SubjectId   { get; init; }   // who was evaluated
    public string?        SubjectType { get; init; }   // the kind of ID SubjectId holds, e.g. "user", "device"
    public string?        VariantName { get; init; }   // the variant received; "control" for outsiders and ineligible subjects
    public bool           IsEligible  { get; init; }   // false: excluded by a filter
    public bool           IsOutsider  { get; init; }   // true: hash fell outside all allocations (sees control behavior)
    public DateTimeOffset Timestamp   { get; init; }
    public string?        ConfigRevision { get; init; }   // fingerprint of the experiment's definition at that moment
    public IReadOnlyDictionary<string, object>? Context { get; init; }   // the attributes the experiment lists in exposureContext
}
```

Nothing is de-duplicated: every evaluation is an exposure, so a subject has many.

### Exposure context

Analysis often needs the attributes a subject was evaluated with (country, plan, platform). Each experiment says which ones to record by listing their keys in its config:

```json
{ "id": "checkout-flow", ..., "exposureContext": [ "country", "plan" ] }
```

Every exposure of that experiment then carries those attributes in `Context`:

```csharp
exposure.Context   // { "country": "BR", "plan": "premium" }
```

- Values come from `KomentoOptions.StaticContext` and the context passed with the call, with the call's value winning, the same merge conversions use.
- A listed key missing from the evaluation is left out. `Context` is `null` when the experiment lists no keys or none were present.
- Ineligible subjects and outsiders get the context too.
- Only the listed keys are recorded, so attributes that are personal data stay off the event unless you list them.
- The list is part of the definition, so changing it changes the [config revision](#config-revision).

### Config revision

`ConfigRevision` is a short fingerprint of the experiment's definition: id, subject type, variants and allocations, filters and overrides. Equal definitions give equal revisions, in any process. It changes when the definition changes, so you can tell exposures that ran under different rules apart. For example, group by `(FlagKey, ConfigRevision)` before comparing variants, or discard the exposures from before an allocation was edited.

Segment membership is not part of the definition. A config that says "segment `beta-users` gets `treatment`" keeps its revision when someone joins `beta-users` in the segment store. To analyze membership changes, use the exposure's `Timestamp`.

### Reading the stream directly

Skip `Komento.Sinks` if you want your own pipeline (for example to push into Kafka). Resolve `IExposureStream` and read it:

```csharp
var stream = provider.GetRequiredService<IExposureStream>();
await foreach (var exposure in stream.Reader.ReadAllAsync(ct))
{
    // write it somewhere
}
```

There is a single reader, so run one consumer. To fan out to several destinations use [sinks](sinks.md). `Reader` throws an `InvalidOperationException` naming the option when the stream is off.

Evaluation never waits on the stream. It is bounded (`ExposureChannelCapacity`, default 4096); a full stream drops the exposure and counts it in `komento.exposures.dropped`.

## Conversions

A conversion is something a subject did that you want to measure an experiment on: a purchase, a signup, a click. Record one with `IExperimentTracker`, natively or through OpenFeature.

Enable the stream (off by default; `Track` is a no-op until it is on):

```csharp
services.AddKomento(o =>
    {
        o.EnableTrackStream = true;
        o.StaticContext     = EvaluationContext.Create().Set("service", "checkout").Build();
    })
    .AddTrackSink<MyTrackSink>();          // or AddLoggingTrackSink() to see what flows
```

**Natively:**

```csharp
tracker.Track("purchase", userId, ctx, value: 49.90,
              properties: new Dictionary<string, object?> { ["currency"] = "BRL" });
```

`ctx` is the same `EvaluationContext` you pass to `GetVariantAsync`, so one prebuilt context serves both calls. There is also an overload without it.

**Through OpenFeature** (`KomentoFeatureProvider` forwards the call to the tracker):

```csharp
featureClient.Track("purchase", evalCtx,
    TrackingEventDetails.Builder().SetValue(49.90).Set("currency", "BRL").Build());
```

The tracker is injected into the provider automatically when it is registered in DI. This needs the `OpenFeature` package at 2.9.0 or later. See [OpenFeature](openfeature.md#tracking-conversions).

### The conversion event

```csharp
public readonly struct TrackEvent
{
    public string?                               EventName  { get; init; }
    public string?                               SubjectId  { get; init; }
    public double?                               Value      { get; init; }   // an amount, a duration, ...
    public IReadOnlyDictionary<string, object?>? Properties { get; init; }   // the fields passed with the call
    public IReadOnlyDictionary<string, object>?  Context    { get; init; }   // static context + per-call context
    public DateTimeOffset                        Timestamp  { get; init; }
}
```

- `Context` is `KomentoOptions.StaticContext` plus the per-call context, with per-call attributes taking precedence. With OpenFeature, the per-call context is the one the SDK assembled from its global, transaction, client and invocation layers. Set `IncludeContextInTrackEvents = false` to leave it off the event when attributes are personal data.
- Conversions are keyed by `SubjectId`. When subjects come in more than one kind, add a `subjectType` attribute to the context so sinks can join on the right kind.
- The native `Track` throws `ArgumentException` for a missing event name or subject ID. A call arriving through OpenFeature without a targeting key (or without an event name) is skipped and counted in `komento.track.dropped` with `reason="no_subject"` (or `"no_event_name"`).
- A full track channel (`TrackChannelCapacity`, default 4096) drops the event and counts it in `komento.track.dropped` with `reason="queue_full"`.

## Measuring results

To say whether a variant "won", join exposures with conversions on the subject:

1. Keep only exposures where `IsEligible && !IsOutsider`. Outsiders and ineligible subjects are not in the experiment.
2. For each subject and experiment, take the **first** exposure. Every evaluation is an exposure, so a subject has many; the first one is the one that counts.
3. Count conversions that happened **after** that first exposure, grouped by `VariantName`, and compare rates between variants.

Things that commonly break the analysis:

- **The subject ID must match.** Conversions must use the same ID you passed to `GetVariantAsync` (with ASP.NET Core, the value your `ISubjectProvider` returns). Anonymous-to-logged-in ID changes break the join. When subjects come in more than one kind, use `SubjectType` on the exposure and add a `subjectType` attribute to the context you pass to `Track`, so you can join on both.
- **Evaluate at the point of use.** An evaluation counts as an exposure even if the user never saw the feature. Evaluate where the user would actually see the variant, not at startup.
- **Dropped events bias results.** Check the drop counters before trusting numbers.

Compute significance in your analytics tool or with a stats library, on the joined data.

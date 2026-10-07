# Extension Points

Komento is designed around twelve public interfaces. Each covers one seam: six in the evaluation pipeline, two for getting exposure data out of it, three for recording conversions, and one for observing configuration changes. This document explains why each exists and what problem it is meant to solve — intended as a reference for deciding which implementations to build.

---

## `IExperimentSource`

```csharp
public interface IExperimentSource
{
    ValueTask<IReadOnlyDictionary<string, ExperimentConfig>> LoadAsync(
        IReadOnlySet<string> experimentIds,
        CancellationToken ct = default);
}
```

**Why it exists:** Experiment definitions need to come from somewhere. The engine itself has no opinion on where configs are stored — that is the source's job. `LoadAsync` is called once at startup by `InitializeKomentoAsync` and hands a full snapshot to the engine.

**What it solves:** Decouples config storage from the evaluation engine. The engine does not know or care whether configs live in a JSON file, a database row, an HTTP response, or a message queue payload.

**`experimentIds` parameter:** The set of experiment IDs the caller wants. **An empty set means "all experiments"** — that is what `InitializeKomentoAsync` passes. A non-empty set (for example one given to the polling service) asks for only those IDs; filtering server-side where possible avoids pulling the entire experiment catalogue.

**Production notes:**
- The built-in `AppSettingsExperimentSource` reads from `IConfiguration`. This is suitable for local development and integration tests only.
- A production source will typically call an internal config service, query a database table, or read from a distributed cache.
- `LoadAsync` runs at startup and when `AddPeriodicRefresh` triggers a refresh, away from the hot path, so I/O and allocations are fine here.
- `Komento.Http` provides a ready-made HTTP source, and `InMemoryExperimentSource` covers tests. See [Configuration](configuration.md#sources).

---

## `IConfigUpdater`

```csharp
public interface IConfigUpdater
{
    ValueTask UpdateAsync(IReadOnlyDictionary<string, ExperimentConfig> configs, IReadOnlySet<string> experimentIds, CancellationToken ct = default);
    ValueTask UpdateAsync(IReadOnlyDictionary<string, ExperimentConfig> configs, CancellationToken ct = default);
    ValueTask UpdateAsync(ExperimentConfig config, CancellationToken ct = default);
    ValueTask RemoveAsync(string experimentId, CancellationToken ct = default);
}
```

**Why it exists:** The engine needs to swap configs at runtime without restarting the process. `IConfigUpdater` is the write side — the mechanism through which config changes reach the in-memory engine.

**What it solves:** Enables hot reload. The engine (`ExperimentClient`) implements this interface. External systems — polling services, message consumers, webhook handlers — inject `IConfigUpdater` and call `UpdateAsync` when they detect a change.

**`experimentIds` overload:** Passing a non-empty set loads only the configs whose ID is in the set; an empty set (or the overload without the parameter) loads everything in `configs`. Use it when a feed carries more experiments than this service runs, so the engine never compiles configs it doesn't need.

**Atomicity:** Each `UpdateAsync` call builds a new `FrozenDictionary` and atomically swaps the reference. In-flight evaluations finish against the previous config; all subsequent calls see the new one. There is no lock contention on the read path.

**Production notes:**
- A push model (message queue, Redis Pub/Sub, webhook) calls `UpdateAsync(config)` for individual changes.
- A poll model (background service on a timer) calls `UpdateAsync(configs)` with a full refresh each cycle.
- `RemoveAsync` is for flag retirement — removing an experiment stops it returning any variant (falls back to `NotFound`).

---

## `ISegmentProvider`

```csharp
public interface ISegmentProvider
{
    ValueTask<bool> IsInSegmentAsync(string subjectId, string segmentName, CancellationToken ct = default);
}
```

**Why it exists:** Experiments can gate eligibility on segment membership — "only users in the `beta-users` segment see this variant." The engine needs to ask "is this subject in this segment?" without knowing how segments are stored.

**What it solves:** Decouples segment storage from evaluation. The engine calls `IsInSegmentAsync` during filter and override evaluation when a `SegmentIncludeFilter` or `SegmentOverride` is present on an experiment.

**Hot path warning:** This method IS on the hot path. Every call to `GetVariantAsync` for an experiment with segment operations will call it. Implementations must be fast:
- Static lists: sort + binary search in memory (O log n), zero allocations. Use the built-in `InMemorySegmentProvider`, registered with `AddSegmentProvider(instance)`.
- Dynamic lists: a local in-process cache with a short TTL (30–60 seconds) in front of the real store, so a database or HTTP call happens at most once per TTL per subject.

**Production notes:**
- The built-in `InMemorySegmentProvider` uses BinSets (sorted binary arrays, binary search). It is allocation-free and O(log n). It is suitable for static lists loaded at startup — millions of IDs are practical.
- For dynamic segments (membership changes frequently), implement a provider backed by Redis Sets, a database bitmap, or a Bloom filter, with a local cache layer.
- The interface is intentionally minimal. The engine tracks which experiments use which segments; the provider only answers membership.

---

## `ISubjectProvider` *(Komento.AspNetCore)*

```csharp
public interface ISubjectProvider
{
    string  SubjectType { get; }
    string? GetSubject(HttpContext context);
}
```

**Why it exists:** In HTTP contexts, the engine needs to know *who* the current request is for. The subject identifier (user ID, tenant ID, device ID, etc.) lives somewhere in the request — JWT claim, session cookie, header, query parameter — but the engine has no opinion on how it is extracted.

**What it solves:** Decouples subject identity extraction from evaluation. The `[RequireVariant]` action filter and `.RequireVariant()` endpoint filter both use registered `ISubjectProvider` implementations to resolve the subject before calling the engine.

**`SubjectType`:** Providers declare which subject type they serve. The integration matches the provider to the experiment by comparing `ISubjectProvider.SubjectType` with `ExperimentConfig.SubjectType`. Multiple providers can be registered simultaneously — one for users, one for tenants, one for devices.

**Return `null`:** When no subject can be resolved (unauthenticated request, missing header), return `null`. The filter treats null as a miss and returns `404 Not Found`.

**Production notes:**
- One provider per subject type. If all your experiments use `"user"`, one provider is enough.
- Keep `GetSubject` synchronous. It runs on every gated request. Do not call external services here.
- Common implementations: read `ClaimTypes.NameIdentifier` from `context.User`, read a header, read a session value.

---

## `IEvaluationContextEnricher` *(Komento.AspNetCore)*

```csharp
public interface IEvaluationContextEnricher
{
    ValueTask EnrichAsync(HttpContext context, EvaluationContextBuilder builder, CancellationToken ct = default);
}
```

**Why it exists:** Filter evaluation often needs attributes beyond a subject ID — locale, plan tier, country, platform, feature flags already assigned. These attributes come from different places (request headers, JWT claims, profile services) and are assembled per-request.

**What it solves:** Gives a pipeline for building the per-request `EvaluationContext`. Multiple enrichers are registered and run in order before each evaluation triggered by `[RequireVariant]` or `.RequireVariant()`. Each enricher adds its slice of attributes.

**Ordering matters:** Enrichers run in registration order. A later enricher can overwrite attributes set by an earlier one. Register enrichers from cheapest to most expensive.

**Production notes:**
- Fast, synchronous enrichers (reading from already-parsed JWT claims, from headers) should return `ValueTask.CompletedTask`.
- Async enrichers (calling Redis for a profile, calling a feature store) are supported but add latency to every gated request. Cache aggressively.
- The static context set in `KomentoOptions.StaticContext` (region, service name, environment) is applied by the engine to every evaluation, at the lowest precedence. Enricher attributes take precedence over a static attribute of the same name.
- Think of enrichers as the assembly point for "what do we know about this request that experiments might filter on?"
- `KomentoRequestContext.ResolveAsync(httpContext, ct)` runs the registered subject providers and enrichers for a request and returns the subject ID and the enriched context. The gating filters use it, and endpoints that evaluate by hand can use it instead of repeating that logic.

---

## `IExperimentClient`

```csharp
public interface IExperimentClient
{
    ValueTask<VariantResult> GetVariantAsync(string flagKey, string subjectId, in EvaluationContext ctx, CancellationToken ct = default);

    ValueTask<bool>   GetBoolAsync  (string flagKey, string subjectId, in EvaluationContext ctx, bool   defaultValue = default, CancellationToken ct = default);
    ValueTask<string> GetStringAsync(string flagKey, string subjectId, in EvaluationContext ctx, string defaultValue = "",      CancellationToken ct = default);
    ValueTask<int>    GetIntAsync   (string flagKey, string subjectId, in EvaluationContext ctx, int    defaultValue = default, CancellationToken ct = default);
    ValueTask<double> GetDoubleAsync(string flagKey, string subjectId, in EvaluationContext ctx, double defaultValue = default, CancellationToken ct = default);

    bool ExperimentExists(string flagKey);
}
```

**Why it exists:** This is the primary read surface — the interface consumers call to evaluate a flag. It is separated from the engine's write surface (`IConfigUpdater`) so that application code only ever holds a reference to the read side.

**What it solves:** Defines the contract for flag evaluation independently of the engine implementation. Application code, ASP.NET Core filters, and OpenFeature adapters all depend on this interface; the concrete `ExperimentClient` is internal.

**`GetVariantAsync` is canonical.** The typed helpers (`GetBoolAsync`, etc.) call it internally and unwrap `VariantResult.Value`. Use `GetVariantAsync` when you need the full result (eligibility, outsider status, variant name, how the variant was assigned in `Source`: `Hash`, `SubjectOverride` or `SegmentOverride`, and the experiment's `SubjectType`). Use the typed helpers for simple on/off flags with a typed payload.

**`ExperimentExists`:** A fast existence check for adapters that need to distinguish "flag not found" from "subject ineligible". `Komento.OpenFeature` uses this to emit OpenFeature's `FLAG_NOT_FOUND` only for genuinely missing experiments.

**`in EvaluationContext`:** The context is passed by reference (no struct copy). Build it once per request or operation and pass it through.

**Production notes:**
- The concrete engine is registered as a singleton under both `IExperimentClient` and `IConfigUpdater`. All application code should inject `IExperimentClient` only.
- In unit tests, stub or mock `IExperimentClient` to force specific variants without running the real engine. The interface is simple enough that a hand-written stub is usually cleaner than a mock:

```csharp
public sealed class StubExperimentClient : IExperimentClient
{
    private readonly Dictionary<string, string> _forced = new(StringComparer.Ordinal);

    public StubExperimentClient Force(string flag, string variant)
    {
        _forced[flag] = variant;
        return this;
    }

    public ValueTask<VariantResult> GetVariantAsync(
        string flagKey, string subjectId, in EvaluationContext ctx, CancellationToken ct = default)
    {
        var name = _forced.GetValueOrDefault(flagKey, "control");
        return ValueTask.FromResult(new VariantResult { VariantName = name, IsEligible = true });
    }

    public ValueTask<bool>   GetBoolAsync  (string f, string s, in EvaluationContext c, bool   d = default, CancellationToken ct = default) => ValueTask.FromResult(d);
    public ValueTask<string> GetStringAsync(string f, string s, in EvaluationContext c, string d = "",      CancellationToken ct = default) => ValueTask.FromResult(d);
    public ValueTask<int>    GetIntAsync   (string f, string s, in EvaluationContext c, int    d = default, CancellationToken ct = default) => ValueTask.FromResult(d);
    public ValueTask<double> GetDoubleAsync(string f, string s, in EvaluationContext c, double d = default, CancellationToken ct = default) => ValueTask.FromResult(d);

    public bool ExperimentExists(string flagKey) => _forced.ContainsKey(flagKey);
}
```
- `GetVariantAsync` is allocation-free on the synchronous fast path (no segment operations). It allocates only when a truly async segment provider is involved.

---

## `IExposureStream`

```csharp
public interface IExposureStream
{
    ChannelReader<ExposureEvent> Reader { get; }
}

public readonly struct ExposureEvent
{
    public string?        FlagKey     { get; init; }
    public string?        SubjectId   { get; init; }
    public string?        SubjectType { get; init; }
    public string?        VariantName { get; init; }
    public bool           IsEligible  { get; init; }
    public bool           IsOutsider  { get; init; }
    public DateTimeOffset Timestamp   { get; init; }
    public string?        ConfigRevision { get; init; }
    public IReadOnlyDictionary<string, object>? Context { get; init; }
}
```

**Why it exists:** To tell whether a variant won you need to know who was assigned to which one. Every evaluation is an *exposure*, and the engine has to hand that record to something outside the process without slowing evaluation down.

**What it solves:** Gets exposure data out of the hot path. The engine writes each exposure to a bounded channel with a non-blocking `TryWrite`; whatever reads `Reader` runs on its own schedule. If the channel is full the exposure is dropped and counted in `komento.exposures.dropped` — evaluation never waits on a consumer.

**Opt-in:** Off by default. Set `KomentoOptions.EnableExposureStream = true`. With it off, nothing is buffered (a channel with no reader would only fill up and drop), and reading `Reader` throws an `InvalidOperationException` that names the option.

**Single reader.** A channel has one logical consumer. To fan out to several destinations, use `Komento.Sinks` (below) rather than reading the stream from several places.

**Fields worth knowing:**
- `SubjectType` says which kind of ID `SubjectId` holds (`"user"`, `"device"`, ...). Only join exposures with conversion events keyed on the same kind of ID.
- Outsiders (`IsOutsider`) and ineligible subjects (`!IsEligible`) are recorded too, with `VariantName == "control"`. Filter them out when analyzing.
- `ConfigRevision` fingerprints the experiment's definition, so exposures recorded under different rules can be told apart. It does not cover segment membership. See [Exposures and conversions](exposures-and-conversions.md#config-revision).
- `Context` holds the evaluation-context attributes the experiment lists in its `exposureContext`; it is null when none are listed. See [Exposures and conversions](exposures-and-conversions.md#exposure-context).
- Nothing is de-duplicated: every evaluation is an exposure. Analysis normally takes the first exposure per subject and experiment.

**Production notes:**
- Capacity is `KomentoOptions.ExposureChannelCapacity` (default 4096). Size it for bursts, and alert on `komento.exposures.dropped`: if it is non-zero, the data is incomplete and any analysis of it is biased.
- Reading it directly (`await foreach (var e in stream.Reader.ReadAllAsync(ct))`) is right when you already have a pipeline to push into. Otherwise prefer `IExposureSink`.

---

## `IExposureSink` *(Komento.Sinks)*

```csharp
public interface IExposureSink
{
    ValueTask WriteAsync(IReadOnlyList<ExposureEvent> batch, CancellationToken ct);
}
```

**Why it exists:** Most destinations (a warehouse, an events service, a message queue) want batches, and every one needs the same plumbing: batching by size and time, a bounded buffer, a timeout, error handling, and a flush on shutdown. You should only write the part that is specific to your destination.

**What it solves:** `Komento.Sinks` reads `IExposureStream` once and runs each registered sink in its own isolated pipeline: its own bounded queue of batches, its own timeout, and a sequential consumer. A slow or failing sink affects only itself — other sinks and evaluation carry on.

```csharp
services.AddKomento(o => o.EnableExposureStream = true)       // required
        .AddExposureSink<MySink>(o => o.BatchSize = 200)      // resolved from DI as a singleton
        .AddLoggingExposureSink();                            // built in: one structured log entry per exposure
```

**Contract:**
- Calls are **sequential per sink**, so no locking is needed.
- `ct` is cancelled when `WriteTimeout` elapses, or when shutdown gives up on the sink after `ShutdownFlushTimeout`. Pass it through.
- **Throwing drops the batch.** It is logged and counted in `komento.exposures.sink.dropped` (`reason=write_failed` or `write_timeout`), and later batches still arrive. Retrying belongs inside the sink.
- Every registered sink receives every exposure.

**Production notes:**
- A sink is created once, as a singleton, from the root service provider, so it must not depend on scoped services.
- Options per sink: `Name`, `BatchSize`, `FlushInterval`, `MaxPendingBatches`, `WriteTimeout`, `ShutdownFlushTimeout`. When a sink falls behind, its queue fills and further batches are dropped for that sink only (`reason=queue_full`).
- On shutdown each sink gets `ShutdownFlushTimeout` to write what is pending, then is abandoned. A sink that ignores its token and blocks forever cannot be killed; it fills its queue, then drops. Watch the drop counters.
- Requires `Komento.Sinks` and `EnableExposureStream`. The host fails at startup with a clear message if the stream is not enabled. [Sinks](sinks.md) has a worked example.

---

## `IExperimentTracker`

```csharp
public interface IExperimentTracker
{
    void Track(string eventName, string subjectId, in EvaluationContext ctx,
               double? value = null, IReadOnlyDictionary<string, object?>? properties = null);

    void Track(string eventName, string subjectId,
               double? value = null, IReadOnlyDictionary<string, object?>? properties = null);
}
```

**Why it exists:** An experiment is judged by what subjects do afterwards. `Track` records that, next to the exposure data, without leaving the process or blocking the caller.

**What it solves:** A non-blocking way to record a conversion natively, with the same evaluation context you pass to `GetVariantAsync`. The engine adds `KomentoOptions.StaticContext` underneath (per-call attributes take precedence), stamps the time from `TimeProvider`, copies the properties, and queues a `TrackEvent` on its own bounded channel.

**Production notes:**
- It is a narrow interface on the same engine singleton as `IExperimentClient`, so existing implementations and stubs of `IExperimentClient` are unaffected.
- `Track` does nothing unless `KomentoOptions.EnableTrackStream = true`. A missing event name or subject ID throws `ArgumentException`.
- `KomentoFeatureProvider` forwards OpenFeature's `Track` to it, so both entry points produce the same event.

---

## `ITrackStream`

```csharp
public interface ITrackStream
{
    ChannelReader<TrackEvent> Reader { get; }
}
```

**Why it exists and what it solves:** The read side for conversions. It is a separate channel from `IExposureStream`, so conversions keep their own room however many exposures arrive, and each stream has its own capacity (`TrackChannelCapacity`), drop counter and opt-in.

**Production notes:** The stream has a single reader, like the exposure stream; use `ITrackSink` for several destinations. `Reader` throws an `InvalidOperationException` that names `EnableTrackStream` when the stream is off. A full channel drops the event and counts it in `komento.track.dropped`.

---

## `ITrackSink` *(Komento.Sinks)*

```csharp
public interface ITrackSink
{
    ValueTask WriteAsync(IReadOnlyList<TrackEvent> batch, CancellationToken ct);
}
```

**Why it exists and what it solves:** What `IExposureSink` does for exposures, for conversions: batching, per-sink isolation, timeouts, shutdown flush and drop counting, with only the destination-specific write left to you.

**Contract and production notes:** The contract matches `IExposureSink`: calls are sequential per sink, throwing drops the batch, retries belong inside the sink, and the sink is a singleton resolved from the root provider. Register with `AddTrackSink<T>()`, a delegate overload, or `AddLoggingTrackSink()`; it needs `EnableTrackStream`. Options are `TrackSinkOptions` (the same properties as `ExposureSinkOptions`, both derived from `SinkOptions`). Drops are counted in `komento.track.sink.dropped`.

---

## `IConfigChanges`

```csharp
public interface IConfigChanges
{
    event Action<IReadOnlyCollection<string>> Changed;
}
```

**Why it exists:** Code that reacts to experiment changes (a cache of derived data, an OpenFeature client waiting for `ProviderConfigurationChanged`) needs to know *when* configs change and *which* ones, without polling the engine.

**What it solves:** The engine compares each config it receives with the one it holds, structurally (id, subject type, variants, filters, overrides), and raises `Changed` with the ids that were added, removed or really changed. `IConfigUpdater.UpdateAsync` and `RemoveAsync` are the only sources of change, and the same instance implements both.

**Contract and production notes:**
- `Changed` is raised once per update call, with every id that changed in it. A full reload that changes three experiments raises it once with three ids.
- An update that changes nothing raises nothing, so a polling service that reloads the same configs every interval is silent.
- When two parts cannot be compared reliably (a variant value type without value equality, say), they count as changed, so a real change is never missed.
- Handlers run synchronously on the thread that applied the update, so keep them quick. A handler that throws is ignored and does not affect the update or other handlers.
- `KomentoFeatureProvider` subscribes to turn the ids into OpenFeature's `ProviderConfigurationChanged` event. See [OpenFeature](openfeature.md).

---

## Exposure telemetry that needs no extension point

Two outputs are built in and need no implementation from you:

- **Metrics.** A `System.Diagnostics.Metrics` meter named `Komento` (`komento.exposures`, tagged by `experiment`, `variant` and `outcome`, and `komento.exposures.dropped`). Always on. Tags carry only bounded values, which keeps metric cardinality low; per-subject detail lives in the exposure stream and sinks. Export with `.AddMeter("Komento")`.
- **Activity events.** When an `Activity` is current and recording (for example the ASP.NET Core request span), each exposure is added to it as a `feature_flag.evaluation` event following the OpenTelemetry feature-flag convention. On by default (`EmitActivityEvents`); the subject ID is included as `feature_flag.context.id` unless `IncludeSubjectIdInActivityEvents = false`.

`ExposureEvent.Timestamp` (on the stream) comes from the registered `TimeProvider`, which tests can replace with a fake clock.

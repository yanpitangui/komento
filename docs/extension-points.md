# Extension Points

Komento is designed around eight public interfaces. Each covers one seam: six in the evaluation pipeline, and two for getting exposure data out of it. This document explains why each exists and what problem it is meant to solve — intended as a reference for deciding which implementations to build.

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
- `LoadAsync` is not on the hot path — it only runs at startup (and when a polling service triggers a refresh). I/O and allocations are acceptable here.

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
- Static lists: sort + binary search in memory (O log n), zero allocations. Use the built-in `InMemorySegmentProvider`.
- Dynamic lists: a local in-process cache with a short TTL (30–60 seconds) in front of the real store. Never call a database or HTTP endpoint inline without caching.

**Production notes:**
- The built-in `InMemorySegmentProvider` uses BinSets (sorted binary arrays, binary search). It is allocation-free and O(log n). It is suitable for static lists loaded at startup — millions of IDs are practical.
- For dynamic segments (membership changes frequently), implement a provider backed by Redis Sets, a database bitmap, or a Bloom filter, with a local cache layer.
- The interface is intentionally minimal. The provider is not responsible for knowing which experiments use which segments — the engine handles that.

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

**What it solves:** Defines the contract for flag evaluation independently of the engine implementation. Application code, ASP.NET Core filters, and OpenFeature adapters all depend on this interface — never on the concrete `ExperimentClient`.

**`GetVariantAsync` is canonical.** The typed helpers (`GetBoolAsync`, etc.) call it internally and unwrap `VariantResult.Value`. Use `GetVariantAsync` when you need the full result (eligibility, outsider status, variant name). Use the typed helpers for simple on/off flags with a typed payload.

**`ExperimentExists`:** A fast existence check for adapters that need to distinguish "flag not found" from "subject ineligible". `Komento.OpenFeature` uses this to emit OpenFeature's `FLAG_NOT_FOUND` only for genuinely missing experiments.

**`in EvaluationContext`:** The context is passed by reference (no struct copy). Build it once per request or operation and pass it through.

**Production notes:**
- The concrete engine is registered as a singleton under both `IExperimentClient` and `IConfigUpdater`. All application code should inject `IExperimentClient` only.
- In unit tests, stub or mock `IExperimentClient` to force specific variants without running the real engine. The interface is simple enough that a hand-written stub is usually cleaner than a mock.
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
}
```

**Why it exists:** To tell whether a variant won you need to know who was assigned to which one. Every evaluation is an *exposure*, and the engine has to hand that record to something outside the process without slowing evaluation down.

**What it solves:** Gets exposure data out of the hot path. The engine writes each exposure to a bounded channel with a non-blocking `TryWrite`; whatever reads `Reader` runs on its own schedule. If the channel is full the exposure is dropped and counted in `komento.exposures.dropped` — evaluation never waits on a consumer.

**Opt-in:** Off by default. Set `KomentoOptions.EnableExposureStream = true`. With it off, nothing is buffered (a channel with no reader would only fill up and drop), and reading `Reader` throws an `InvalidOperationException` that names the option.

**Single reader.** A channel has one logical consumer. To fan out to several destinations, use `Komento.Exposure` (below) rather than reading the stream from several places.

**Fields worth knowing:**
- `SubjectType` says which kind of ID `SubjectId` holds (`"user"`, `"device"`, ...). Only join exposures with conversion events keyed on the same kind of ID.
- Outsiders (`IsOutsider`) and ineligible subjects (`!IsEligible`) are recorded too, with `VariantName == "control"`. Filter them out when analyzing.
- Nothing is de-duplicated: every evaluation is an exposure. Analysis normally takes the first exposure per subject and experiment.

**Production notes:**
- Capacity is `KomentoOptions.ExposureChannelCapacity` (default 4096). Size it for bursts, and alert on `komento.exposures.dropped`: if it is non-zero, the data is incomplete and any analysis of it is biased.
- Reading it directly (`await foreach (var e in stream.Reader.ReadAllAsync(ct))`) is right when you already have a pipeline to push into. Otherwise prefer `IExposureSink`.

---

## `IExposureSink` *(Komento.Exposure)*

```csharp
public interface IExposureSink
{
    ValueTask WriteAsync(IReadOnlyList<ExposureEvent> batch, CancellationToken ct);
}
```

**Why it exists:** Most destinations (a warehouse, an events service, a message queue) want batches, and every one needs the same plumbing: batching by size and time, a bounded buffer, a timeout, error handling, and a flush on shutdown. You should only write the part that is specific to your destination.

**What it solves:** `Komento.Exposure` reads `IExposureStream` once and runs each registered sink in its own isolated pipeline: its own bounded queue of batches, its own timeout, and a sequential consumer. A slow or failing sink affects only itself — other sinks and evaluation carry on.

```csharp
services.AddKomento(o => o.EnableExposureStream = true)       // required
        .AddExposureSink<MySink>(o => o.BatchSize = 200)      // resolved from DI as a singleton
        .AddLoggingExposureSink();                            // built in: one structured log entry per exposure
```

**Contract:**
- Calls are **sequential per sink** — `WriteAsync` is never called concurrently for the same sink, so no locking is needed.
- `ct` is cancelled when `WriteTimeout` elapses, or when shutdown gives up on the sink after `ShutdownFlushTimeout`. Pass it through.
- **Throwing drops the batch.** It is logged and counted in `komento.exposures.sink.dropped` (`reason=write_failed` or `write_timeout`), and later batches still arrive. Retrying belongs inside the sink.
- Every registered sink receives every exposure.

**Production notes:**
- A sink is created once, as a singleton, from the root service provider, so it must not depend on scoped services.
- Options per sink: `Name`, `BatchSize`, `FlushInterval`, `MaxPendingBatches`, `WriteTimeout`, `ShutdownFlushTimeout`. When a sink falls behind, its queue fills and further batches are dropped for that sink only (`reason=queue_full`).
- On shutdown each sink gets `ShutdownFlushTimeout` to write what is pending, then is abandoned. A sink that ignores its token and blocks forever cannot be killed; it fills its queue, then drops. Watch the drop counters.
- Requires `Komento.Exposure` and `EnableExposureStream`. The host fails at startup with a clear message if the stream is not enabled. The README's "Exposures and metrics" section has a worked example.

---

## Exposure telemetry that needs no extension point

Two outputs are built in and need no implementation from you:

- **Metrics.** A `System.Diagnostics.Metrics` meter named `Komento` (`komento.exposures`, tagged by `experiment`, `variant` and `outcome`, and `komento.exposures.dropped`). Always on. Tags carry only bounded values, which keeps metric cardinality low; per-subject detail lives in the exposure stream and sinks. Export with `.AddMeter("Komento")`.
- **Activity events.** When an `Activity` is current and recording (for example the ASP.NET Core request span), each exposure is added to it as a `feature_flag.evaluation` event following the OpenTelemetry feature-flag convention. On by default (`EmitActivityEvents`); the subject ID is included as `feature_flag.context.id` unless `IncludeSubjectIdInActivityEvents = false`.

`ExposureEvent.Timestamp` (on the stream) comes from the registered `TimeProvider`, which tests can replace with a fake clock.

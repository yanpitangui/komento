# Extension Points

Komento is designed around six public interfaces. Each covers one seam in the evaluation pipeline. This document explains why each exists and what problem it is meant to solve — intended as a reference for deciding which implementations to build.

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

**`experimentIds` parameter:** The source receives the set of experiment IDs this service declared in `KomentoOptions.Experiments`. It should only return configs for those IDs — filtering server-side where possible avoids pulling the entire experiment catalogue.

**Production notes:**
- The built-in `AppSettingsExperimentSource` reads from `IConfiguration`. This is suitable for local development and integration tests only.
- A production source will typically call an internal config service, query a database table, or read from a distributed cache.
- `LoadAsync` is not on the hot path — it only runs at startup (and when a polling service triggers a refresh). I/O and allocations are acceptable here.

---

## `IConfigUpdater`

```csharp
public interface IConfigUpdater
{
    IReadOnlySet<string> RelevantExperimentIds { get; }

    ValueTask UpdateAsync(IReadOnlyDictionary<string, ExperimentConfig> configs, CancellationToken ct = default);
    ValueTask UpdateAsync(ExperimentConfig config, CancellationToken ct = default);
    ValueTask RemoveAsync(string experimentId, CancellationToken ct = default);
}
```

**Why it exists:** The engine needs to swap configs at runtime without restarting the process. `IConfigUpdater` is the write side — the mechanism through which config changes reach the in-memory engine.

**What it solves:** Enables hot reload. The engine (`ExperimentClient`) implements this interface. External systems — polling services, message consumers, webhook handlers — inject `IConfigUpdater` and call `UpdateAsync` when they detect a change.

**`RelevantExperimentIds`:** Returns the set declared in `KomentoOptions.Experiments`. Callers (Kafka consumers, Redis subscribers, etc.) should filter incoming change events against this set before calling `UpdateAsync`, so the engine never processes changes for experiments it doesn't run.

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
- The static context set in `KomentoOptions.StaticContext` (region, service name, environment) is merged first. Enricher attributes layer on top.
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
}
```

**Why it exists:** This is the primary read surface — the interface consumers call to evaluate a flag. It is separated from the engine's write surface (`IConfigUpdater`) so that application code only ever holds a reference to the read side.

**What it solves:** Defines the contract for flag evaluation independently of the engine implementation. Application code, ASP.NET Core filters, and OpenFeature adapters all depend on this interface — never on the concrete `ExperimentClient`.

**`GetVariantAsync` is canonical.** The typed helpers (`GetBoolAsync`, etc.) call it internally and unwrap `VariantResult.Value`. Use `GetVariantAsync` when you need the full result (eligibility, outsider status, variant name). Use the typed helpers for simple on/off flags with a typed payload.

**`in EvaluationContext`:** The context is passed by reference (no struct copy). Build it once per request or operation and pass it through.

**Production notes:**
- The concrete engine is registered as a singleton under both `IExperimentClient` and `IConfigUpdater`. All application code should inject `IExperimentClient` only.
- In unit tests, stub or mock `IExperimentClient` to force specific variants without running the real engine. The interface is simple enough that a hand-written stub is usually cleaner than a mock.
- `GetVariantAsync` is allocation-free on the synchronous fast path (no segment operations). It allocates only when a truly async segment provider is involved.

# Komento

A storage-agnostic experimentation and feature flag engine for .NET. Its primary operation is the *experiment check*: given a flag key and a subject, deterministically return which variant they belong to.

```csharp
var result = await client.GetVariantAsync("checkout-flow", userId, ctx);
if (result == "treatment")
    return NewCheckout();
```

**Design goals**

- Zero I/O in the hot path — all evaluation is in-memory.
- Near-zero allocations — `ValueTask`, `Span<T>`, `FrozenDictionary`, `ArrayPool<T>`.
- Pluggable at every seam — swap in your own config source, segment store, subject resolver, or context enricher.
- OpenFeature support via the `Komento.OpenFeature` provider package.

---

## Packages

| Package | Purpose | NuGet |
|---|---|---|
| Komento | Core engine, all interfaces, DI registration | [![NuGet](https://img.shields.io/nuget/v/Komento.svg)](https://www.nuget.org/packages/Komento) |
| Komento.AspNetCore | ASP.NET Core integration (filters, subject provider, enrichers) | [![NuGet](https://img.shields.io/nuget/v/Komento.AspNetCore.svg)](https://www.nuget.org/packages/Komento.AspNetCore) |
| Komento.OpenFeature | OpenFeature provider adapter | [![NuGet](https://img.shields.io/nuget/v/Komento.OpenFeature.svg)](https://www.nuget.org/packages/Komento.OpenFeature) |
| Komento.OpenFeature.AspNetCore | Per-request OpenFeature context built from Komento's ASP.NET Core enrichers | [![NuGet](https://img.shields.io/nuget/v/Komento.OpenFeature.AspNetCore.svg)](https://www.nuget.org/packages/Komento.OpenFeature.AspNetCore) |
| Komento.Sinks | Batched, isolated sinks for exposures and conversions (R3) | [![NuGet](https://img.shields.io/nuget/v/Komento.Sinks.svg)](https://www.nuget.org/packages/Komento.Sinks) |
---

## Quick start

### 1. Register Komento

```csharp
builder.Services
    .AddKomento(options =>
    {
        // Declare which experiments this service cares about.
        options.Experiments = new HashSet<string> { "checkout-flow", "dark-mode" };

        // Attributes merged into every EvaluationContext at evaluation time.
        options.StaticContext = EvaluationContext.Create()
            .Set("region", "BR")
            .Set("service", "payments")
            .Build();
    })
    .AddSource<AppSettingsExperimentSource>(); // built-in: reads from appsettings.json
```

### 2. Load configs at startup

Call `InitializeKomentoAsync` once before accepting traffic. It calls `IExperimentSource.LoadAsync` and feeds the result into the engine.

```csharp
await app.Services.InitializeKomentoAsync();
await app.RunAsync();
```

### 3. Evaluate

Inject `IExperimentClient` anywhere:

```csharp
public class CheckoutService(IExperimentClient experiments)
{
    public async Task<IActionResult> Checkout(string userId)
    {
        var ctx = EvaluationContext.Create().Set("platform", "web").Build();
        var variant = await experiments.GetVariantAsync("checkout-flow", userId, ctx);

        return variant == "treatment" ? NewFlow() : LegacyFlow();
    }
}
```

Typed helpers are available for simple flag cases:

```csharp
bool enabled = await experiments.GetBoolAsync("dark-mode", userId, ctx);
string theme  = await experiments.GetStringAsync("ui-theme", userId, ctx, defaultValue: "default");
```

### 4. Use Komento through OpenFeature

If your application already uses the OpenFeature .NET SDK, install `Komento.OpenFeature` and register `KomentoFeatureProvider` with the OpenFeature API. Registering it loads the experiment configs from your source.

```csharp
using Komento.OpenFeature;
using OpenFeature;
using OpenFeature.Model;

services.AddKomento().AddSource<MySource>();
services.AddSingleton<KomentoFeatureProvider>();

await Api.Instance.SetProviderAsync(app.Services.GetRequiredService<KomentoFeatureProvider>());

var client = Api.Instance.GetClient();
var ctx = EvaluationContext.Builder()
    .SetTargetingKey(userId)
    .Set("platform", new Value("web"))
    .Build();

bool enabled = await client.GetBooleanValueAsync("dark-mode", false, ctx);
string theme = await client.GetStringValueAsync("ui-theme", "default", ctx);
```

Provider lifecycle, change events, resolution details, a per-request context for ASP.NET Core, and telemetry are covered in [OpenFeature](docs/openfeature.md).

### 5. Record exposures and conversions (optional)

To know who saw which variant, register an exposure sink or export the built-in metrics. See [Exposures and metrics](#exposures-and-metrics). To record what subjects did afterwards, use `IExperimentTracker`. See [Conversions](#conversions).

---

## Configuration format (`appsettings.json`)

```json
{
  "Komento": {
    "Experiments": [
      {
        "id": "checkout-flow",
        "subjectType": "user",
        "variants": [
          { "name": "control",   "allocation": 0.5 },
          { "name": "treatment", "allocation": 0.5, "value": true }
        ],
        "globalFilters": [
          { "type": "trait-equals",    "key": "country", "value": "BR" },
          { "type": "segment-include", "segment": "beta-users" }
        ],
        "overrides": [
          { "type": "subject", "subjectId": "user-42",       "variant": "treatment" },
          { "type": "segment", "segment":  "internal-staff", "variant": "treatment" }
        ]
      }
    ]
  }
}
```

**Allocation** is a `double` in `[0.0, 1.0]`. Allocations across all variants must sum to ≤ 1.0. Subjects whose hash falls outside all allocations are *outsiders* — they see control behavior but are excluded from experiment metrics.

**Filter types**

| `type` | Fields | Description |
|---|---|---|
| `trait-equals` | `key`, `value` | Subject must have `key = value` in `EvaluationContext` |
| `segment-include` | `segment` | Subject must be a member of the named segment |

**Override types**

| `type` | Fields | Description |
|---|---|---|
| `subject` | `subjectId`, `variant` | Forces a specific subject into a variant, before bucket assignment |
| `segment` | `segment`, `variant` | Forces all members of a segment into a variant, before bucket assignment |

---

## Assignment result

`GetVariantAsync` always returns a `VariantResult`:

```csharp
public readonly struct VariantResult
{
    public string  VariantName { get; init; }
    public object? Value       { get; init; }  // optional typed payload from VariantConfig.Value
    public bool    IsEligible  { get; init; }  // false when a global filter excluded the subject
    public bool    IsOutsider  { get; init; }  // true when no variant bucket matched
}
```

The `==` operator compares against a variant name string directly:

```csharp
if (result == "treatment") { ... }
```

**Fallback table**

| Situation | `VariantName` | `IsEligible` | `IsOutsider` |
|---|---|---|---|
| Experiment not found | `"control"` | `false` | `false` |
| Subject failed a global filter | `"control"` | `false` | `false` |
| Subject outside all buckets | `"control"` | `true` | `true` |
| Normal assignment | variant name | `true` | `false` |

---

## Extension points

Komento is designed to be extended rather than forked. The six interfaces below are the complete seam set.

---

### `IExperimentSource` — config loading

```csharp
public interface IExperimentSource
{
    ValueTask<IReadOnlyDictionary<string, ExperimentConfig>> LoadAsync(
        IReadOnlySet<string> experimentIds,
        CancellationToken ct = default);
}
```

**When to implement:** You store experiment definitions somewhere other than `appsettings.json` — a database, an HTTP API, a Redis key, a gRPC service. Implement `IExperimentSource` to pull the initial config at startup.

`LoadAsync` is called once by `InitializeKomentoAsync`. It receives the set of experiment IDs declared in `KomentoOptions.Experiments` so you only fetch what this service cares about.

**Built-in:** `AppSettingsExperimentSource` reads from `IConfiguration`. Good for local development and tests; not suitable for production systems where configs live in a database or a dedicated config service.

**Registration:**

```csharp
builder.Services
    .AddKomento(o => o.Experiments = [...])
    .AddSource<MyDatabaseExperimentSource>();
```

**Example — HTTP source:**

```csharp
public sealed class HttpExperimentSource(HttpClient http) : IExperimentSource
{
    public async ValueTask<IReadOnlyDictionary<string, ExperimentConfig>> LoadAsync(
        IReadOnlySet<string> experimentIds, CancellationToken ct)
    {
        var response = await http.GetFromJsonAsync<List<ExperimentConfig>>(
            $"/experiments?ids={string.Join(',', experimentIds)}", ct);

        return response?.ToDictionary(e => e.Id)
               ?? (IReadOnlyDictionary<string, ExperimentConfig>)new Dictionary<string, ExperimentConfig>();
    }
}
```

---

### `IConfigUpdater` — hot config reload

```csharp
public interface IConfigUpdater
{
    IReadOnlySet<string> RelevantExperimentIds { get; }

    ValueTask UpdateAsync(IReadOnlyDictionary<string, ExperimentConfig> configs, CancellationToken ct = default);
    ValueTask UpdateAsync(ExperimentConfig config, CancellationToken ct = default);
    ValueTask RemoveAsync(string experimentId, CancellationToken ct = default);
}
```

**When to use:** Push config changes to the engine at runtime without restarting. The engine (`ExperimentClient`) implements this interface — inject it as `IConfigUpdater` to drive hot reloads from wherever change notifications arrive.

`RelevantExperimentIds` returns the set declared in `KomentoOptions.Experiments`. External notifiers (message queues, Kafka consumers, WebSocket listeners) should filter against it so they don't process changes for experiments this service doesn't run.

The update is atomic: the engine builds a new `FrozenDictionary` and swaps the reference in one step. In-flight evaluations complete against the previous config; all subsequent calls see the new one.

**Example — background polling service:**

```csharp
public sealed class ExperimentPollingService(
    IExperimentSource source,
    IConfigUpdater updater,
    ILogger<ExperimentPollingService> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var configs = await source.LoadAsync(updater.RelevantExperimentIds, ct);
                await updater.UpdateAsync(configs, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogError(ex, "Failed to refresh experiment configs");
            }

            await Task.Delay(TimeSpan.FromSeconds(30), ct);
        }
    }
}
```

Register it alongside Komento:

```csharp
builder.Services.AddHostedService<ExperimentPollingService>();
```

**Example — push notification (e.g., Kafka, Redis Pub/Sub):**

```csharp
// In your message consumer handler:
if (updater.RelevantExperimentIds.Contains(incomingConfig.Id))
    await updater.UpdateAsync(incomingConfig, ct);
```

---

### `ISegmentProvider` — set membership

```csharp
public interface ISegmentProvider
{
    ValueTask<bool> IsInSegmentAsync(string subjectId, string segmentName, CancellationToken ct = default);
}
```

**When to implement:** You have large subject ID lists (allowlists, holdouts, beta cohorts) that need membership checks during filter or override evaluation. The engine calls this whenever a `SegmentIncludeFilter` or `SegmentOverride` appears in an experiment config.

This method is on the hot path. For static lists loaded at startup, implement it with binary search over a sorted in-memory array. For dynamic lists, a local cache with a short TTL is strongly recommended.

**Built-in:** `InMemorySegmentProvider` — loaded at startup with a `Dictionary<string, IEnumerable<string>>`. Internally uses **BinSets**: sorted, deduplicated byte arrays where membership is a binary search, O(log n), allocation-free. Ideal for static lists that fit in RAM; millions of IDs are practical.

**Registration:**

```csharp
builder.Services
    .AddKomento(o => o.Experiments = [...])
    .AddSegmentProvider<MyRedisSegmentProvider>();
```

**Example — Redis-backed provider with local cache:**

```csharp
public sealed class RedisSegmentProvider(IDatabase redis) : ISegmentProvider
{
    private readonly ConcurrentDictionary<string, (DateTimeOffset Expires, bool Result)> _cache = new();

    public async ValueTask<bool> IsInSegmentAsync(string subjectId, string segmentName, CancellationToken ct)
    {
        var key = $"{segmentName}:{subjectId}";
        if (_cache.TryGetValue(key, out var cached) && cached.Expires > DateTimeOffset.UtcNow)
            return cached.Result;

        var isMember = await redis.SetContainsAsync(segmentName, subjectId);
        _cache[key] = (DateTimeOffset.UtcNow.AddSeconds(30), isMember);
        return isMember;
    }
}
```

---

### `ISubjectProvider` *(Komento.AspNetCore)* — HTTP subject resolution

```csharp
public interface ISubjectProvider
{
    string  SubjectType { get; }
    string? GetSubject(HttpContext context);
}
```

**When to implement:** You need the `[RequireVariant]` filter or `.RequireVariant()` endpoint filter to know *who* the current HTTP request is for. The integration matches providers to experiments by `SubjectType` — an experiment with `subjectType = "user"` is evaluated using the provider whose `SubjectType` is `"user"`.

Return `null` when no subject can be resolved (unauthenticated request, missing header, etc.). The filter treats a null subject as a miss and returns `404 Not Found`.

Register multiple providers to support different subject types:

```csharp
builder.Services
    .AddKomentoAspNetCore()
    .AddSubjectProvider<UserSubjectProvider>()    // SubjectType = "user"
    .AddSubjectProvider<TenantSubjectProvider>(); // SubjectType = "tenant"
```

**Example — JWT claims provider:**

```csharp
public sealed class UserSubjectProvider : ISubjectProvider
{
    public string SubjectType => "user";

    public string? GetSubject(HttpContext context)
        => context.User.FindFirstValue(ClaimTypes.NameIdentifier);
}
```

**Example — API key header provider:**

```csharp
public sealed class TenantSubjectProvider : ISubjectProvider
{
    public string SubjectType => "tenant";

    public string? GetSubject(HttpContext context)
        => context.Request.Headers["X-Tenant-Id"].FirstOrDefault();
}
```

---

### `IEvaluationContextEnricher` *(Komento.AspNetCore)* — per-request context

```csharp
public interface IEvaluationContextEnricher
{
    ValueTask EnrichAsync(HttpContext context, EvaluationContextBuilder builder, CancellationToken ct = default);
}
```

**When to implement:** You need attributes beyond the static context (region, service name) to evaluate filters for HTTP requests — locale from the `Accept-Language` header, plan tier from JWT claims, country from a GeoIP lookup. Enrichers run in registration order before every evaluation triggered by `[RequireVariant]` or `.RequireVariant()`.

Enrichers are synchronous-friendly — return `ValueTask.CompletedTask` if no async work is needed. If an enricher must call an external service, use `async`/`await` as normal.

**Registration:**

```csharp
builder.Services
    .AddKomentoAspNetCore()
    .AddEnricher<LocaleEnricher>()
    .AddEnricher<ClaimsEnricher>();
```

**Example — locale from `Accept-Language`:**

```csharp
public sealed class LocaleEnricher : IEvaluationContextEnricher
{
    public ValueTask EnrichAsync(HttpContext context, EvaluationContextBuilder builder, CancellationToken ct)
    {
        var locale = context.Request.Headers.AcceptLanguage.FirstOrDefault()?.Split(',')[0].Trim();
        if (locale is not null)
            builder.Set("locale", locale);
        return ValueTask.CompletedTask;
    }
}
```

**Example — plan tier from JWT claims:**

```csharp
public sealed class ClaimsEnricher : IEvaluationContextEnricher
{
    public ValueTask EnrichAsync(HttpContext context, EvaluationContextBuilder builder, CancellationToken ct)
    {
        var plan = context.User.FindFirstValue("plan");
        if (plan is not null)
            builder.Set("plan", plan);
        return ValueTask.CompletedTask;
    }
}
```

---

### `IExperimentClient` — evaluation

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

**When to implement:** Testing — mock or stub `IExperimentClient` to force variants in unit tests without running the real engine.

The concrete implementation (`ExperimentClient`) is registered as a singleton under both `IExperimentClient` and `IConfigUpdater`. It is internal; use the interfaces.

**Example — test stub:**

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
        string flagKey, string subjectId, in EvaluationContext ctx, CancellationToken ct)
    {
        var name = _forced.GetValueOrDefault(flagKey, "control");
        return ValueTask.FromResult(new VariantResult { VariantName = name, IsEligible = true });
    }

    public ValueTask<bool>   GetBoolAsync  (string f, string s, in EvaluationContext c, bool   d, CancellationToken ct) => ValueTask.FromResult(d);
    public ValueTask<string> GetStringAsync(string f, string s, in EvaluationContext c, string d, CancellationToken ct) => ValueTask.FromResult(d);
    public ValueTask<int>    GetIntAsync   (string f, string s, in EvaluationContext c, int    d, CancellationToken ct) => ValueTask.FromResult(d);
    public ValueTask<double> GetDoubleAsync(string f, string s, in EvaluationContext c, double d, CancellationToken ct) => ValueTask.FromResult(d);
}
```

---

## ASP.NET Core integration

### Setup

```csharp
builder.Services
    .AddKomento(o => { o.Experiments = ["checkout-flow"]; })
    .AddSource<AppSettingsExperimentSource>();

builder.Services
    .AddKomentoAspNetCore()
    .AddSubjectProvider<UserSubjectProvider>()
    .AddEnricher<LocaleEnricher>();
```

### `[RequireVariant]` — MVC action filter

Gate a controller action on a variant assignment. Returns `404 Not Found` when the subject is not in the required variant.

```csharp
[HttpGet("new-checkout")]
[RequireVariant("checkout-flow", "treatment")]
public IActionResult NewCheckout() => View();
```

Applied to an entire controller to gate all actions:

```csharp
[RequireVariant("admin-ui", "enabled")]
[ApiController, Route("admin")]
public class AdminController : ControllerBase { ... }
```

### `.RequireVariant()` — minimal API endpoint filter

```csharp
app.MapGet("/new-checkout", NewCheckoutHandler)
   .RequireVariant("checkout-flow", "treatment");
```

---

## EvaluationContext

`EvaluationContext` is an immutable snapshot of attributes used to evaluate filters. It is passed `in` everywhere — no struct copy on the hot path.

```csharp
// Build a context:
var ctx = EvaluationContext.Create()
    .Set("platform", "android")
    .Set("country", "BR")
    .Build();

// Extend an existing context (e.g., layering per-request data onto a base):
var extended = EvaluationContextBuilder.CreateFrom(baseCtx)
    .Set("locale", "pt-BR")
    .Build();
```

Attributes set via `KomentoOptions.StaticContext` apply to every evaluation, whichever way it is made: `IExperimentClient` directly, the ASP.NET Core filters, or the OpenFeature provider. They sit at the lowest precedence: an attribute passed with the call (from enrichers, or the OpenFeature evaluation context) takes precedence over a static attribute of the same name.

---

## Exposures and metrics

An *exposure* is recorded every time Komento evaluates an experiment for a subject. Exposures tell you who was assigned which variant, which is the half of an experiment you need to compare outcomes. Komento surfaces them two ways:

| | Metrics | Exposure sinks |
|---|---|---|
| For | Dashboards and alerts: "how many exposures per variant?" | Analysis: "which subject saw which variant, and when?" |
| Needs | Nothing (always on) | `Komento.Sinks` and `EnableExposureStream` |
| Contains subject IDs | No | Yes |

Komento does not analyze results. It gives you clean data to send wherever you do (a warehouse, an events pipeline, a product-analytics tool).

### Metrics

A `System.Diagnostics.Metrics` meter named `Komento` is always on:

| Instrument | Tags | Meaning |
|---|---|---|
| `komento.exposures` | `experiment`, `variant`, `outcome` (`assigned` / `outsider` / `ineligible`) | Exposures per variant |
| `komento.exposures.dropped` | `experiment` | Exposures dropped because the exposure stream was full |
| `komento.exposures.sink.dropped` | `sink`, `reason` (`queue_full` / `write_failed` / `write_timeout`) | Exposures a sink did not receive |
| `komento.track.dropped` | `reason` (`queue_full` / `no_subject` / `no_event_name`) | Conversion events dropped before reaching a sink (see [Conversions](#conversions)) |
| `komento.track.sink.dropped` | `sink`, `reason` (`queue_full` / `write_failed` / `write_timeout`) | Conversion events a sink did not receive |

Outsiders and ineligible subjects are reported with `variant="control"`, so filter on `outcome="assigned"` to count exposures to real variants. Tags carry only bounded values (experiment, variant, outcome), which keeps metric cardinality low; per-subject detail lives in the exposure stream and sinks. To export with OpenTelemetry:

```csharp
builder.Services.AddOpenTelemetry()
    .WithMetrics(m => m
        .AddMeter("Komento")
        .AddPrometheusExporter());   // or AddOtlpExporter(), etc.
```

### Traces

Each exposure is also recorded as a `feature_flag.evaluation` event on `Activity.Current` (for example the ASP.NET Core request span), following the [OpenTelemetry feature-flag convention](https://opentelemetry.io/docs/specs/semconv/feature-flags/feature-flags-events/). That ties "which variant did this request get" to the rest of the trace, with no extra setup. Nothing is recorded when there is no active trace, or it is not being sampled.

| Tag | Value |
|---|---|
| `feature_flag.key` | The experiment id |
| `feature_flag.result.variant` | The variant received (`control` for outsiders and ineligible subjects) |
| `feature_flag.result.reason` | `split` for an assigned variant, otherwise `default` |
| `feature_flag.provider.name` | `Komento` |
| `feature_flag.context.id` | The subject ID |
| `komento.outcome` | `assigned`, `outsider` or `ineligible` |
| `komento.subject_type` | The experiment's subject type |

The subject ID is recorded by default. If your subject IDs are personal data that must not appear in traces (traces are often retained and shared differently from application data), exclude it:

```csharp
services.AddKomento(o => o.IncludeSubjectIdInActivityEvents = false);
```

Turn the event off entirely with `EmitActivityEvents = false`.

### Sending exposures somewhere

**1. Install the package.**

```
dotnet add package Komento.Sinks
```

**2. Write a sink.** A sink receives batches of exposures and sends them to your destination. This one posts them to an HTTP endpoint:

```csharp
public sealed class HttpExposureSink(IHttpClientFactory httpClientFactory) : IExposureSink
{
    public async ValueTask WriteAsync(IReadOnlyList<ExposureEvent> batch, CancellationToken ct)
    {
        var http     = httpClientFactory.CreateClient("exposures");
        var response = await http.PostAsJsonAsync("/v1/exposures", batch, ct);
        response.EnsureSuccessStatusCode();   // throwing drops this batch (it is logged and counted)
    }
}
```

Rules for sinks:
- `WriteAsync` is never called concurrently for the same sink, so you don't need locking.
- Pass `ct` through. It is cancelled when `WriteTimeout` elapses, or when shutdown gives up on the sink after `ShutdownFlushTimeout`.
- If it throws or times out, that batch is dropped and later batches still arrive. Do any retrying inside the sink.
- The sink is created once as a singleton from the root service provider, so it can't depend on scoped services.

**3. Register it.** Enable the stream and add the sink wherever you call `AddKomento`:

```csharp
builder.Services.AddHttpClient("exposures", c => c.BaseAddress = new Uri("https://events.example.com"));

builder.Services
    .AddKomento(o => o.EnableExposureStream = true)      // required: sinks read this stream
    .AddExposureSink<HttpExposureSink>(o =>
    {
        o.BatchSize     = 200;                            // flush when 200 are buffered...
        o.FlushInterval = TimeSpan.FromSeconds(5);        // ...or after 5 seconds, whichever is first
    });
```

Sinks run in a background service, so your app must run under a .NET generic host (ASP.NET Core and `Host.CreateApplicationBuilder` both do). If you build the container by hand, start the registered `IHostedService`s yourself. If `EnableExposureStream` is not set, the host fails at startup with a message saying so.

**4. Check that it works.** Add the built-in logging sink next to yours and evaluate an experiment. You should see one entry per exposure:

```csharp
.AddLoggingExposureSink()
// info: Komento.Sinks.Exposure
//       Exposure checkout-button subject user-42 -> treatment (eligible: True, outsider: False) at 2026-10-06T12:00:00.0000000+00:00
```

Remove it once your real sink works; it is useful for local development.

You can register as many sinks as you like (`AddExposureSink<A>().AddExposureSink<B>()`, or a delegate: `.AddExposureSink(async (batch, ct) => await ...)`). Each one gets every exposure.

### Sink options

| Option | Default | Meaning |
|---|---|---|
| `Name` | type name, or `sink-{index}` | The `sink` tag on metrics and the name in logs |
| `BatchSize` | 100 | Max exposures per `WriteAsync` call |
| `FlushInterval` | 5 s | Flush a partial batch after this long |
| `MaxPendingBatches` | 64 | Batches buffered while the sink is busy |
| `WriteTimeout` | 30 s | The write's token is cancelled after this; the batch is dropped |
| `ShutdownFlushTimeout` | 10 s | How long shutdown waits for this sink to flush |

### How sinks behave under load

Evaluation never waits on a sink. Komento writes each exposure to a bounded stream (`ExposureChannelCapacity`, default 4096); if it is full, the exposure is dropped and counted in `komento.exposures.dropped`. Each sink then has its own queue, so a slow or stuck sink only affects itself: when its `MaxPendingBatches` fill, its new batches are dropped and counted in `komento.exposures.sink.dropped`, and the other sinks are unaffected. On shutdown each sink gets `ShutdownFlushTimeout` to write what is pending, then is abandoned. A sink that ignores its cancellation token and blocks forever cannot be killed, so it will fill its queue and then drop; watch the drop counters.

Alert on `komento.exposures.dropped` and `komento.exposures.sink.dropped`. If either is non-zero, the data in your sink is incomplete and any analysis of it is biased.

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
}
```

### Using exposures to measure results

To say whether a variant "won", join exposures with conversions (a purchase, a click, a signup) on the subject. Record conversions with `IExperimentTracker` (see [Conversions](#conversions)) or use the ones you already collect:

1. Keep only exposures where `IsEligible && !IsOutsider`. Outsiders and ineligible subjects are not in the experiment.
2. For each subject and experiment, take the **first** exposure. Every evaluation is an exposure, so a subject has many; the first one is the one that counts.
3. Count conversions that happened **after** that first exposure, grouped by `VariantName`, and compare rates between variants.

Things that commonly break the analysis:
- **The subject ID must match.** Conversions must use the same ID you passed to `GetVariantAsync` (with ASP.NET Core, the value your `ISubjectProvider` returns). Anonymous-to-logged-in ID changes break the join. When subjects come in more than one kind, use `SubjectType` on the exposure and add a `subjectType` attribute to the context you pass to `Track`, so you can join on both.
- **Evaluate at the point of use.** An evaluation counts as an exposure even if the user never saw the feature. Evaluate where the user would actually see the variant, not at startup.
- **Dropped events bias results.** Check the drop counters before trusting numbers.

Compute significance in your analytics tool or with a stats library, on the joined data.

### Reading the stream directly

Skip `Komento.Sinks` if you want your own pipeline (for example to push into Kafka). With `EnableExposureStream = true`, resolve `IExposureStream` and read it:

```csharp
var stream = provider.GetRequiredService<IExposureStream>();
await foreach (var exposure in stream.Reader.ReadAllAsync(ct))
{
    // write it somewhere
}
```

There is a single reader, so run one consumer. The stream is disabled by default; with no consumer nothing is buffered.

## Conversions

A *conversion* is something a subject did that you want to measure an experiment on: a purchase, a signup, a click. Record one with `IExperimentTracker`, natively or through OpenFeature, and deliver it with track sinks. Komento gives you conversion events to join with exposures; the results themselves are computed in your analytics tool.

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

The tracker is injected into the provider automatically when it is registered in DI. This needs the `OpenFeature` package at 2.9.0 or later.

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

### Track sinks

Track sinks work like exposure sinks: `ITrackSink.WriteAsync(IReadOnlyList<TrackEvent>, CancellationToken)`, one isolated queue per sink, the same options (`BatchSize`, `FlushInterval`, `MaxPendingBatches`, `WriteTimeout`, `ShutdownFlushTimeout`) and the same failure behavior. Register with `AddTrackSink<T>()`, a delegate overload, or `AddLoggingTrackSink()`; it needs `EnableTrackStream = true`, and the host fails at startup with a clear message otherwise. Drops are counted in `komento.track.sink.dropped` (tags `sink` and `reason`). Alert on both drop counters: a non-zero count means the data is incomplete.

## Performance

All read operations — variant lookup, filter evaluation, segment membership — are allocation-free on the hot path. BenchmarkDotNet (`MemoryDiagnoser`) shows **0 bytes allocated** per `GetVariantAsync` call across all paths including the async segment filter path.

Key mechanisms:

- `FrozenDictionary<string, CompiledExperiment>` — experiment map, lock-free reads.
- `ArrayPool<byte>` — XxHash64 input buffer, no heap allocation per hash.
- `ValueTask<T>` — synchronous results wrapped without `Task` allocation.
- `in EvaluationContext` — struct passed by reference, never copied.
- No LINQ, no closures in any hot-path method.

Config updates (`UpdateAsync`) are the only write operation. They build a new `FrozenDictionary` on a background thread and atomically swap the reference — in-flight reads are unaffected.

---

## Development

```bash
# Build
dotnet build

# Tests
dotnet test

# Single test
dotnet test --filter "FullyQualifiedName~Unknown_experiment_returns_NotFound"

# Benchmarks (Release mode required)
dotnet run --project benchmarks/Komento.Benchmarks/ -c Release
```

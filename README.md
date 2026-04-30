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
- OpenFeature-compatible surface in mind for future alignment.

---

## Packages

| Package | Purpose |
|---|---|
| `Komento` | Core engine, all interfaces, DI registration |
| `Komento.AspNetCore` | `[RequireVariant]` action filter, `.RequireVariant()` endpoint filter, subject provider and context enricher abstractions |

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

Attributes set via `KomentoOptions.StaticContext` are merged automatically in `Komento.AspNetCore` before each evaluation. Request-level attributes from enrichers layer on top.

---

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
dotnet test --filter "FullyQualifiedName~MyTestName"

# Benchmarks (Release mode required)
dotnet run --project tests/Komento.Benchmarks/ -c Release
```

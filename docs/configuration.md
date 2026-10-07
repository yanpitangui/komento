# Configuration

Where experiment definitions come from, what they look like, and how to change them while the service runs.

## Experiment format

The built-in `appsettings.json` source reads the `Komento:Experiments` array. Other sources produce the same `ExperimentConfig` objects.

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

**Allocation** is a `double` in `[0.0, 1.0]`. Allocations across all variants must sum to at most 1.0. Subjects whose hash falls outside all allocations are *outsiders*: they see control behavior and are left out of experiment metrics.

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

How these rules combine is described in [Concepts](concepts.md#how-a-subject-is-assigned).

## Sources

A source loads the experiment definitions. Register one with `AddSource`, then call `InitializeKomentoAsync()` once before accepting traffic. It calls `IExperimentSource.LoadAsync` and feeds the result to the engine.

```csharp
builder.Services.AddKomento().AddSource<AppSettingsExperimentSource>();

var app = builder.Build();
await app.Services.InitializeKomentoAsync();
await app.RunAsync();
```

### Built in

| Source | Reads from | Use for |
|---|---|---|
| `AppSettingsExperimentSource` | `IConfiguration`, section `Komento` | Local development, small deployments |
| `InMemoryExperimentSource` | Configs you set in code | Tests, fixed configs |

```csharp
services.AddKomento().AddSource(new InMemoryExperimentSource().Set(new ExperimentConfig
{
    Id          = "checkout-flow",
    SubjectType = "user",
    Variants    =
    [
        new VariantConfig { Name = "control",   Allocation = 0.5 },
        new VariantConfig { Name = "treatment", Allocation = 0.5, Value = true }
    ]
}));
```

### HTTP: `Komento.Http`

`AddHttpSource<TResponse>` GETs the base address of a named `HttpClient`, deserializes the body as `TResponse`, and maps it to configs:

```csharp
public sealed record ExperimentsResponse(List<ExperimentConfig> Experiments);

services.AddHttpClient("Komento.Http", c => c.BaseAddress = new Uri("https://config.example.com/experiments"));

services.AddKomento()
        .AddHttpSource<ExperimentsResponse>(response => response.Experiments);
```

Pass a second argument to use a different client name. The named client must have a `BaseAddress`.

### Your own source

Implement [`IExperimentSource`](extension-points.md#iexperimentsource) when definitions live in a database, a config service or a cache:

```csharp
public sealed class DatabaseExperimentSource(HttpClient http) : IExperimentSource
{
    public async ValueTask<IReadOnlyDictionary<string, ExperimentConfig>> LoadAsync(
        IReadOnlySet<string> experimentIds, CancellationToken ct = default)
    {
        // An empty set means "all experiments".
        var configs = await http.GetFromJsonAsync<List<ExperimentConfig>>("/experiments", ct) ?? [];
        return configs
            .Where(c => experimentIds.Count == 0 || experimentIds.Contains(c.Id))
            .ToDictionary(c => c.Id);
    }
}
```

## Changing configs at runtime

### Polling

`AddPeriodicRefresh` reloads from the registered source on a timer and hands the result to the engine:

```csharp
services.AddKomento()
        .AddSource<AppSettingsExperimentSource>()
        .AddPeriodicRefresh(TimeSpan.FromSeconds(30));

// Only some experiments:
services.AddKomento()
        .AddSource<AppSettingsExperimentSource>()
        .AddPeriodicRefresh(TimeSpan.FromMinutes(1), new HashSet<string> { "checkout-flow" });
```

A failed refresh is logged and retried at the next interval. Polling uses the registered `TimeProvider`. A poll that finds the same configs changes nothing and notifies nobody.

### Pushing

When you hear about a change (a message consumer, a webhook), inject [`IConfigUpdater`](extension-points.md#iconfigupdater):

```csharp
await updater.UpdateAsync(changedConfig, ct);
await updater.RemoveAsync("retired-experiment", ct);
```

Each update builds a new immutable map and swaps it in one step. In-flight evaluations finish against the previous configs; later calls see the new ones.

### Reacting to changes

[`IConfigChanges`](extension-points.md#iconfigchanges) raises one event per update with the ids that really changed. The OpenFeature provider turns it into `ProviderConfigurationChanged` ([OpenFeature](openfeature.md#configuration-change-events)).

## Segments

Filters and overrides refer to segments by name. An [`ISegmentProvider`](extension-points.md#isegmentprovider) answers membership.

### Static lists

`InMemorySegmentProvider` holds each list as a `BinSet`: sorted hashes, binary search, no allocation per lookup. Millions of ids are practical.

```csharp
services.AddKomento()
        .AddSegmentProvider(new InMemorySegmentProvider(new Dictionary<string, IEnumerable<string>>
        {
            ["beta-users"]     = ["user-1", "user-2", "user-3"],
            ["internal-staff"] = ["user-42"]
        }));
```

To build your own provider around the same structure, use `BinSet.Build(ids)` and `BinSet.Contains(set, id)`.

### Dynamic lists

Membership is checked on the evaluation path, so put a short-lived local cache in front of the real store:

```csharp
public sealed class CachedSegments(IMembershipStore store, TimeProvider time) : ISegmentProvider
{
    private readonly ConcurrentDictionary<string, (DateTimeOffset Expires, bool Result)> _cache = new();

    public async ValueTask<bool> IsInSegmentAsync(string subjectId, string segmentName, CancellationToken ct = default)
    {
        var key = $"{segmentName}:{subjectId}";
        if (_cache.TryGetValue(key, out var cached) && cached.Expires > time.GetUtcNow())
            return cached.Result;

        var isMember = await store.IsMemberAsync(segmentName, subjectId, ct);
        _cache[key] = (time.GetUtcNow().AddSeconds(30), isMember);
        return isMember;
    }
}

services.AddKomento().AddSegmentProvider<CachedSegments>();
```

## Typed experiment ids

`Komento.Generators` is a source generator that reads a `komento.json` listing your experiment ids and emits constants, so the compiler checks them. Add the file to the project as an `AdditionalFiles` item:

```json
{ "Experiments": ["checkout-flow", "dark-mode"] }
```

```csharp
await experiments.GetVariantAsync(KomentoExperiments.CheckoutFlow.Id, userId, ctx);
```

`KomentoExperiments.AllIds` holds every id.

# Komento

A storage-agnostic experimentation and feature flag engine for .NET. Its primary operation is the *experiment check*: given a flag key and a subject, deterministically return which variant they belong to.

```csharp
var result = await client.GetVariantAsync("checkout-flow", userId, ctx);
if (result == "treatment")
    return NewCheckout();
```

**Design goals**

- Zero I/O in the hot path: all evaluation is in-memory.
- Near-zero allocations: `ValueTask`, `Span<T>`, `FrozenDictionary`, `ArrayPool<T>`.
- Pluggable at every seam: swap in your own config source, segment store, subject resolver, or context enricher.
- Exposure and conversion data you can send anywhere, plus metrics and traces out of the box.
- OpenFeature support via the `Komento.OpenFeature` provider package.

## Packages

| Package | Purpose | NuGet |
|---|---|---|
| Komento | Core engine, all interfaces, DI registration | [![NuGet](https://img.shields.io/nuget/v/Komento.svg)](https://www.nuget.org/packages/Komento) |
| Komento.AspNetCore | ASP.NET Core integration (filters, subject provider, enrichers) | [![NuGet](https://img.shields.io/nuget/v/Komento.AspNetCore.svg)](https://www.nuget.org/packages/Komento.AspNetCore) |
| Komento.Http | Load experiment configs from an HTTP endpoint | [![NuGet](https://img.shields.io/nuget/v/Komento.Http.svg)](https://www.nuget.org/packages/Komento.Http) |
| Komento.Sinks | Batched, isolated sinks for exposures and conversions (R3) | [![NuGet](https://img.shields.io/nuget/v/Komento.Sinks.svg)](https://www.nuget.org/packages/Komento.Sinks) |
| Komento.OpenFeature | OpenFeature provider adapter | [![NuGet](https://img.shields.io/nuget/v/Komento.OpenFeature.svg)](https://www.nuget.org/packages/Komento.OpenFeature) |
| Komento.OpenFeature.AspNetCore | Per-request OpenFeature context built from Komento's ASP.NET Core enrichers | [![NuGet](https://img.shields.io/nuget/v/Komento.OpenFeature.AspNetCore.svg)](https://www.nuget.org/packages/Komento.OpenFeature.AspNetCore) |
| Komento.Generators | Source generator: compile-time constants for experiment ids | [![NuGet](https://img.shields.io/nuget/v/Komento.Generators.svg)](https://www.nuget.org/packages/Komento.Generators) |

## Quick start

**1. Define an experiment** in `appsettings.json`:

```json
{
  "Komento": {
    "Experiments": [
      {
        "id": "checkout-flow",
        "subjectType": "user",
        "variants": [
          { "name": "control",   "allocation": 0.5 },
          { "name": "treatment", "allocation": 0.5 }
        ]
      }
    ]
  }
}
```

**2. Register Komento and load the configs** before accepting traffic:

```csharp
builder.Services
    .AddKomento()
    .AddSource<AppSettingsExperimentSource>();     // reads the "Komento" section

var app = builder.Build();

await app.Services.InitializeKomentoAsync();
await app.RunAsync();
```

**3. Evaluate.** Inject `IExperimentClient` anywhere:

```csharp
public class CheckoutService(IExperimentClient experiments)
{
    public async Task<string> Checkout(string userId)
    {
        var ctx     = EvaluationContext.Create().Set("platform", "web").Build();
        var variant = await experiments.GetVariantAsync("checkout-flow", userId, ctx);

        return variant == "treatment" ? "new flow" : "legacy flow";
    }
}
```

The same subject always gets the same variant. From here, add what you need:

- gate endpoints on a variant: [ASP.NET Core](docs/aspnetcore.md)
- keep using the OpenFeature SDK: [OpenFeature](docs/openfeature.md)
- find out who saw what and what they did next: [Exposures and conversions](docs/exposures-and-conversions.md)

## Documentation

| Page | Covers |
|---|---|
| [Concepts](docs/concepts.md) | How assignment works, the result type, evaluation context, static context |
| [Configuration](docs/configuration.md) | Experiment format, sources, hot reload, segments, typed ids |
| [ASP.NET Core](docs/aspnetcore.md) | `[RequireVariant]`, subject providers, enrichers |
| [OpenFeature](docs/openfeature.md) | The provider: lifecycle, context, details, change events, tracking |
| [Exposures and conversions](docs/exposures-and-conversions.md) | The data an experiment produces and how to measure results |
| [Sinks](docs/sinks.md) | Delivering exposures and conversions to a destination |
| [Metrics and traces](docs/metrics-and-traces.md) | The `Komento` meter and `feature_flag.evaluation` events |
| [Extension points](docs/extension-points.md) | Reference for every public interface |
| [Performance](docs/performance.md) | Allocation behavior and benchmarks |
| [Sample](samples/README.md) | A runnable e-commerce API using all of the above |
| [Contributing](CONTRIBUTING.md) | Build, test, benchmark |

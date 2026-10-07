# OpenFeature

`Komento.OpenFeature` is an [OpenFeature](https://openfeature.dev) provider for the .NET SDK. Code written against the OpenFeature API evaluates Komento experiments, records conversions, and listens for configuration changes without referencing Komento types.

It needs the `OpenFeature` package at version 2.9.0 or later.

## Setup

```csharp
services.AddKomento()
        .AddSource<MyExperimentSource>();
services.AddSingleton<KomentoFeatureProvider>();

var app = builder.Build();
await Api.Instance.SetProviderAsync(app.Services.GetRequiredService<KomentoFeatureProvider>());

var client = Api.Instance.GetClient();
bool enabled = await client.GetBooleanValueAsync("dark-mode", false, context);
```

**Registering the provider loads the configs.** `SetProviderAsync` calls the provider's `InitializeAsync`, which reads the experiments from the registered `IExperimentSource`, the same load `InitializeKomentoAsync()` performs. On the OpenFeature path that call is optional; keep it if parts of your app evaluate through `IExperimentClient` directly.

While the provider is loading, OpenFeature reports it as not ready and evaluations return your default value with `PROVIDER_NOT_READY`. If the source fails, the provider's status becomes an error status and evaluations return your default value, so the failure is visible instead of every flag quietly serving defaults.

When no `IExperimentSource` is registered (for example you push configs with `IConfigUpdater`), the provider is ready immediately.

## Context

OpenFeature merges its context layers before calling the provider: global (`Api.SetContext`), transaction, client, and the context passed to the call, with later layers overriding earlier ones. Komento converts the merged result and, underneath all of it, applies `KomentoOptions.StaticContext`.

The targeting key is the subject ID. Boolean, string and numeric attributes are available to experiment filters.

## A per-request context in ASP.NET Core

Building the same context for every OpenFeature call repeats work your Komento enrichers already do. `Komento.OpenFeature.AspNetCore` runs them once per request and hands the result to OpenFeature as the SDK's transaction context:

```csharp
builder.Services.AddKomentoAspNetCore()
    .AddSubjectProvider<JwtSubjectProvider>()
    .AddEnricher<ClaimsEnricher>();

var app = builder.Build();
app.UseAuthentication();
app.UseAuthorization();
app.UseKomentoOpenFeatureContext();      // after authentication

app.MapGet("/recommendations", async (IFeatureClient featureClient) =>
{
    // No context argument: the subject and plan come from the request.
    var algorithm = await featureClient.GetStringValueAsync("recommendation-algorithm", "collaborative");
    return TypedResults.Ok(algorithm);
});
```

Every OpenFeature evaluation in the request sees the same targeting key and attributes, and so does every `Track` call handled by `KomentoFeatureProvider`. A context passed explicitly to a call takes precedence over the request's. A request with no resolvable subject continues unchanged.

The SDK applies its transaction context to evaluations only; `KomentoFeatureProvider` applies it to `Track` as well, so the two behave the same.

## Resolution details

Each evaluation returns details OpenFeature tools and hooks understand:

| Situation | Reason / error | Notes |
|---|---|---|
| The subject was assigned a variant by hashing into its allocation | `SPLIT` | `Variant` is the variant name |
| An override rule (a specific subject, or a segment) chose the variant | `TARGETING_MATCH` | |
| The subject is ineligible (a filter excluded them) or an outsider (outside every allocation) | `DEFAULT` | the caller's default value; `Variant` is `control` |
| The variant's value is a different type than the one requested | `TYPE_MISMATCH` | `ErrorMessage`, for example `Variant 'on' has a String value, not a Boolean.` |
| The experiment does not exist | `FLAG_NOT_FOUND` | `ErrorMessage` names the flag |
| The context has no targeting key | `TARGETING_KEY_MISSING` | |
| The provider is still loading | `PROVIDER_NOT_READY` | produced by the SDK |

Every result also carries flag metadata:

| Key | Value |
|---|---|
| `outcome` | `assigned`, `outsider` or `ineligible` |
| `subjectType` | the experiment's subject type, such as `user` |

## Configuration change events

When experiments change, the provider emits `ProviderConfigurationChanged` with the ids of the changed flags in `FlagsChanged`:

```csharp
client.AddHandler(ProviderEventTypes.ProviderConfigurationChanged, e =>
{
    foreach (var flag in e.FlagsChanged ?? []) cache.Invalidate(flag);
});
```

- One event is emitted per update, listing every flag that changed in it. A reload that changes three experiments produces one event with three flags.
- Only real changes are announced. A poll that finds the same configs is silent, and so is re-applying an identical config.
- Removing an experiment announces it too.

The engine behind this is [`IConfigChanges`](extension-points.md#iconfigchanges), which you can subscribe to directly without OpenFeature.

## Tracking conversions

`featureClient.Track(...)` is forwarded to Komento's `IExperimentTracker`, so conversions recorded through OpenFeature and through Komento's own API produce the same event and reach the same sinks. See [Conversions](../README.md#conversions).

## Telemetry

Komento adds a `feature_flag.evaluation` event, following the OpenTelemetry feature-flag convention, to the current trace for every evaluation, including evaluations made through OpenFeature. It is on by default (`KomentoOptions.EmitActivityEvents`).

If you would rather have the OpenFeature SDK's own event, register the SDK's `TraceEnricherHook` and turn Komento's event off:

```csharp
services.AddKomento(o => o.EmitActivityEvents = false);
Api.Instance.AddHooks(new TraceEnricherHook(TraceEnricherHookOptions.CreateBuilder().Build()));
```

Pick one. Using both records the event twice.

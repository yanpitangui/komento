# Concepts

How Komento turns a flag key and a subject into a variant.

## The experiment check

```csharp
VariantResult result = await client.GetVariantAsync("checkout-flow", userId, ctx);
if (result == "treatment")
    return NewCheckout();
```

Given an experiment id and a subject id, Komento deterministically returns the variant the subject belongs to. The same subject always gets the same variant for as long as the experiment's configuration is unchanged. Evaluation is in-memory: no I/O happens on this path.

Typed helpers cover the simple cases:

```csharp
bool   enabled = await experiments.GetBoolAsync("dark-mode", userId, ctx);
string theme   = await experiments.GetStringAsync("ui-theme", userId, ctx, defaultValue: "default");
bool   exists  = experiments.ExperimentExists("dark-mode");
```

`GetIntAsync` and `GetDoubleAsync` work the same way. They read the `value` of the assigned variant and return your default when it has none or is a different type. `GetVariantAsync` is the canonical call; the typed helpers use it and unwrap `VariantResult.Value`.

## How a subject is assigned

For each evaluation the engine applies these steps in order and stops at the first that decides:

1. **Subject overrides**: a `subject` override naming this subject id forces its variant.
2. **Global filters**: every filter must pass, otherwise the subject is *ineligible*.
3. **Segment overrides**: a `segment` override whose segment contains the subject forces its variant.
4. **Hash bucket**: the subject id and experiment id are hashed into one of 1000 buckets, and the variants' allocations decide which variant owns that bucket.

Overrides and filters are described in [Configuration](configuration.md#experiment-format).

## Subjects

A *subject* is whoever the experiment is about: a user, a tenant, a device. Each experiment declares a `subjectType`. The id you pass to `GetVariantAsync` must be of that kind. With ASP.NET Core, an [`ISubjectProvider`](aspnetcore.md#subjects) per subject type resolves it from the request.

## Assignment result

`GetVariantAsync` always returns a `VariantResult`:

```csharp
public readonly struct VariantResult
{
    public string  VariantName { get; init; }
    public object? Value       { get; init; }  // optional typed payload from VariantConfig.Value
    public bool    IsEligible  { get; init; }  // false when a global filter excluded the subject
    public bool    IsOutsider  { get; init; }  // true when no variant bucket matched
    public AssignmentSource Source { get; init; }  // Hash, SubjectOverride or SegmentOverride
    public string? SubjectType { get; init; }      // the experiment's subject type
}
```

`Source` and `SubjectType` describe how the variant was reached; they are not part of equality. The `==` operator compares against a variant name directly:

```csharp
if (result == "treatment") { ... }
```

**Fallbacks**

| Situation | `VariantName` | `IsEligible` | `IsOutsider` |
|---|---|---|---|
| Experiment not found | `"control"` | `false` | `false` |
| Subject failed a global filter | `"control"` | `false` | `false` |
| Subject outside all buckets | `"control"` | `true` | `true` |
| Normal assignment | variant name | `true` | `false` |

*Outsiders* are subjects whose hash falls outside every allocation. They see control behavior and are left out of experiment analysis.

## Evaluation context

`EvaluationContext` is an immutable snapshot of attributes that filters read. It is passed `in` everywhere, so the hot path copies nothing.

```csharp
// Build a context:
var ctx = EvaluationContext.Create()
    .Set("platform", "android")
    .Set("country", "BR")
    .Build();

// Layer per-request data onto an existing context:
var extended = EvaluationContextBuilder.CreateFrom(baseCtx)
    .Set("locale", "pt-BR")
    .Build();

// Read what it holds:
foreach (var (key, value) in ctx.Attributes)
    Console.WriteLine($"{key} = {value}");
```

`EvaluationContext.Empty` is the context with no attributes.

### Static context

Attributes set in `KomentoOptions.StaticContext` apply to every evaluation, whichever way it is made: `IExperimentClient` directly, the ASP.NET Core filters, or the OpenFeature provider. They sit at the lowest precedence: an attribute passed with the call (from enrichers, or the OpenFeature evaluation context) takes precedence over a static attribute of the same name.

```csharp
services.AddKomento(o => o.StaticContext = EvaluationContext.Create()
    .Set("region", "BR")
    .Set("service", "payments")
    .Build());
```

Conversion events carry the static context too; see [Exposures and conversions](exposures-and-conversions.md#the-conversion-event).

## Time

Timestamps on exposures and conversions come from the `TimeProvider` registered in the container, so tests can replace it with a fake clock.

## Next

- [Configuration](configuration.md): where experiments come from and how to change them at runtime.
- [Extension points](extension-points.md): every interface you can implement.

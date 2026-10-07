# ASP.NET Core

`Komento.AspNetCore` resolves who a request is for, builds the evaluation context from the request, and gates endpoints on a variant.

## Setup

```csharp
builder.Services.AddKomento().AddSource<AppSettingsExperimentSource>();

builder.Services
    .AddKomentoAspNetCore()
    .AddSubjectProvider<UserSubjectProvider>()      // SubjectType = "user"
    .AddSubjectProvider<TenantSubjectProvider>()    // SubjectType = "tenant"
    .AddEnricher<LocaleEnricher>()
    .AddEnricher<ClaimsEnricher>();
```

## Gating on a variant

`404 Not Found` is returned when the subject is not in the required variant, or when no subject can be resolved.

**MVC controllers and actions**

```csharp
[HttpGet("new-checkout")]
[RequireVariant("checkout-flow", "treatment")]
public IActionResult NewCheckout() => View();

// On a controller, it gates every action:
[RequireVariant("admin-ui", "enabled")]
[ApiController, Route("admin")]
public class AdminController : ControllerBase { ... }
```

**Minimal APIs**

```csharp
app.MapGet("/new-checkout", NewCheckoutHandler)
   .RequireVariant("checkout-flow", "treatment");
```

## Evaluating by hand

When an endpoint needs the variant rather than a gate, `KomentoRequestContext.ResolveAsync` runs the same subject providers and enrichers the filters use and returns the subject id and the enriched context:

```csharp
app.MapGet("/price", async (HttpContext http, IExperimentClient experiments, CancellationToken ct) =>
{
    var (subjectId, ctx) = await KomentoRequestContext.ResolveAsync(http, ct);
    if (subjectId is null) return Results.Unauthorized();

    var price = await experiments.GetStringAsync("price-display", subjectId, ctx, "default", ct: ct);
    return Results.Ok(price);
});
```

## Subjects

An [`ISubjectProvider`](extension-points.md#isubjectprovider-komentoaspnetcore) says who the request is for. Each provider declares a `SubjectType`; an experiment is evaluated with the provider whose type matches the experiment's `subjectType`. Return `null` when there is no subject.

```csharp
public sealed class UserSubjectProvider : ISubjectProvider
{
    public string  SubjectType => "user";
    public string? GetSubject(HttpContext context) => context.User.FindFirstValue(ClaimTypes.NameIdentifier);
}

public sealed class TenantSubjectProvider : ISubjectProvider
{
    public string  SubjectType => "tenant";
    public string? GetSubject(HttpContext context) => context.Request.Headers["X-Tenant-Id"].FirstOrDefault();
}
```

Keep `GetSubject` synchronous: it runs on every gated request.

## Enrichers

An [`IEvaluationContextEnricher`](extension-points.md#ievaluationcontextenricher-komentoaspnetcore) adds attributes for filters to read: locale, plan, country. Enrichers run in registration order, and a later one can overwrite an earlier one's attributes.

```csharp
public sealed class LocaleEnricher : IEvaluationContextEnricher
{
    public ValueTask EnrichAsync(HttpContext context, EvaluationContextBuilder builder, CancellationToken ct = default)
    {
        var locale = context.Request.Headers.AcceptLanguage.FirstOrDefault()?.Split(',')[0].Trim();
        if (locale is not null)
            builder.Set("locale", locale);
        return ValueTask.CompletedTask;
    }
}

public sealed class ClaimsEnricher : IEvaluationContextEnricher
{
    public ValueTask EnrichAsync(HttpContext context, EvaluationContextBuilder builder, CancellationToken ct = default)
    {
        var plan = context.User.FindFirstValue("plan");
        if (plan is not null)
            builder.Set("plan", plan);
        return ValueTask.CompletedTask;
    }
}
```

`KomentoOptions.StaticContext` sits underneath whatever the enrichers set ([Concepts](concepts.md#static-context)).

## With OpenFeature

`Komento.OpenFeature.AspNetCore` runs the same providers and enrichers once per request and hands the result to OpenFeature. See [OpenFeature](openfeature.md#a-per-request-context-in-aspnet-core).

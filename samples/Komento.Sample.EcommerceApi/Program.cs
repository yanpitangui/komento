using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Komento;
using Komento.AspNetCore;
using Komento.OpenFeature;
using Komento.Sample.Contracts;
using Komento.Sample.EcommerceApi.Infrastructure;
using Komento.Sample.EcommerceApi.Komento;
using Komento.Sample.ServiceDefaults;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.IdentityModel.Tokens;
using OpenFeature;
using OpenFeature.Model;

const string JwtIssuer   = "komento-sample";
const string JwtAudience = "komento-sample";
const string JwtSecret   = "komento-sample-secret-key-must-be-at-least-32-chars!";

var builder = WebApplication.CreateBuilder(args);
builder.AddServiceDefaults();
builder.Services.TryAddSingleton(TimeProvider.System);

// Aspire-managed infrastructure
builder.AddNatsClient("nats");
builder.AddNpgsqlDataSource("komento-db");

// Komento core
builder.Services.AddKomento(o =>
{
    o.EnableExposureStream = true;
    o.EnableTrackStream    = true;
    o.StaticContext        = global::Komento.EvaluationContext.Create().Set("service", "ecommerce-api").Build();
})
.AddSource<NatsExperimentSource>()
.AddSegmentProvider<AppSegmentProvider>()
.AddExposureSink<PostgresExposureSink>(o => o.FlushInterval = TimeSpan.FromSeconds(1))
.AddLoggingExposureSink()
.AddTrackSink<PostgresTrackSink>(o => o.FlushInterval = TimeSpan.FromSeconds(1))
.AddLoggingTrackSink();

// Komento.AspNetCore integration
builder.Services.AddKomentoAspNetCore()
    .AddSubjectProvider<JwtSubjectProvider>()
    .AddEnricher<ClaimsEnricher>();

// Infrastructure singletons
builder.Services.AddSingleton<NatsLoyaltyStore>();
builder.Services.AddSingleton<VipBinSetStore>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<VipBinSetStore>());
builder.Services.AddSingleton<DataSeeder>();
builder.Services.AddHostedService<NatsExperimentWatcher>();

// OpenFeature
builder.Services.AddSingleton<KomentoFeatureProvider>();
builder.Services.AddSingleton<IFeatureClient>(_ => Api.Instance.GetClient("ecommerce"));

// JWT authentication
var signingKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(JwtSecret));
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(opts =>
    {
        opts.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer           = true,
            ValidIssuer              = JwtIssuer,
            ValidateAudience         = true,
            ValidAudience            = JwtAudience,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey         = signingKey
        };
    });
builder.Services.AddAuthorization();

var app = builder.Build();

// Startup sequence: seed → Komento init → OpenFeature init
await app.Services.GetRequiredService<DataSeeder>().SeedAsync();
await app.Services.InitializeKomentoAsync();
await Api.Instance.SetProviderAsync(app.Services.GetRequiredService<KomentoFeatureProvider>());

app.MapDefaultEndpoints();
app.UseAuthentication();
app.UseAuthorization();

// ── /token — issue a demo JWT (no auth required) ──────────────────────────

app.MapGet("/token", (string userId, TimeProvider time, string plan = "free") =>
{
    var credentials = new SigningCredentials(signingKey, SecurityAlgorithms.HmacSha256);
    var claims = new[]
    {
        new Claim(JwtRegisteredClaimNames.Sub, userId),
        new Claim("plan", plan)
    };
    var token = new JwtSecurityToken(
        issuer:             JwtIssuer,
        audience:           JwtAudience,
        claims:             claims,
        expires:            time.GetUtcNow().UtcDateTime.AddHours(1),
        signingCredentials: credentials);

    return TypedResults.Ok(new TokenResponse(new JwtSecurityTokenHandler().WriteToken(token)));
});

// ── /products/{id} — uses IExperimentClient + AspNetCore extension points ─

app.MapGet("/products/{id}", async (
    string id,
    HttpContext httpContext,
    IExperimentClient client,
    IEnumerable<ISubjectProvider> subjectProviders,
    IEnumerable<IEvaluationContextEnricher> enrichers,
    CancellationToken ct) =>
{
    string? subjectId = null;
    foreach (var p in subjectProviders)
    {
        subjectId = p.GetSubject(httpContext);
        if (subjectId is not null) break;
    }

    var ctxBuilder = global::Komento.EvaluationContext.Create();
    foreach (var e in enrichers)
        await e.EnrichAsync(httpContext, ctxBuilder, ct);
    var ctx = ctxBuilder.Build();

    var isPremium    = await client.GetBoolAsync  (KomentoExperiments.PremiumProductPage.Id,      subjectId ?? "", ctx, defaultValue: false,     ct: ct);
    var priceVariant = await client.GetStringAsync(KomentoExperiments.PriceDisplay.Id,             subjectId ?? "", ctx, defaultValue: "default", ct: ct);

    var price = priceVariant switch
    {
        "loyalty-price" => 79.99m,
        "vip-price"     => 89.99m,
        _               => 99.99m
    };

    return TypedResults.Ok(new ProductResponse(
        ProductId:    id,
        Name:         $"Komento Widget {id}",
        Price:        price,
        PremiumPage:  isPremium,
        PriceVariant: priceVariant));
}).RequireAuthorization();

// ── /recommendations — uses OpenFeature IFeatureClient ────────────────────

app.MapGet("/recommendations", async (
    HttpContext httpContext,
    IFeatureClient featureClient,
    IEnumerable<ISubjectProvider> subjectProviders,
    CancellationToken ct) =>
{
    var subjectId = ResolveSubject(subjectProviders, httpContext);
    var plan      = httpContext.User.FindFirst("plan")?.Value ?? "free";

    var evalCtx = global::OpenFeature.Model.EvaluationContext.Builder()
        .SetTargetingKey(subjectId)
        .Set("plan", new Value(plan))
        .Build();

    var algo = await featureClient.GetStringValueAsync(KomentoExperiments.RecommendationAlgorithm.Id, "collaborative", evalCtx);

    string[] items = algo == "content-based"
        ? ["Widget A", "Widget B", "Widget C"]
        : ["Widget D", "Widget E", "Widget F"];

    return TypedResults.Ok(new RecommendationsResponse(algo, items));
}).RequireAuthorization();

// ── POST /products/{id}/purchase — records a conversion with IExperimentTracker ──

app.MapPost("/products/{id}/purchase", (
    string id,
    PurchaseRequest purchase,
    HttpContext httpContext,
    IExperimentTracker tracker,
    IEnumerable<ISubjectProvider> subjectProviders) =>
{
    var subjectId = ResolveSubject(subjectProviders, httpContext);
    var plan      = httpContext.User.FindFirst("plan")?.Value ?? "free";

    var ctx = global::Komento.EvaluationContext.Create().Set("plan", plan).Build();
    tracker.Track("purchase", subjectId, in ctx,
        value: (double)purchase.Amount,
        properties: new Dictionary<string, object?> { ["productId"] = id });

    return TypedResults.Ok(new PurchaseResponse(id, purchase.Amount));
}).RequireAuthorization();

// ── POST /recommendations/clicks — records a conversion through OpenFeature's Track API ──

app.MapPost("/recommendations/clicks", (
    RecommendationClickRequest click,
    HttpContext httpContext,
    IFeatureClient featureClient,
    IEnumerable<ISubjectProvider> subjectProviders) =>
{
    var subjectId = ResolveSubject(subjectProviders, httpContext);
    var plan      = httpContext.User.FindFirst("plan")?.Value ?? "free";

    var evalCtx = global::OpenFeature.Model.EvaluationContext.Builder()
        .SetTargetingKey(subjectId)
        .Set("plan", new Value(plan))
        .Build();

    featureClient.Track("recommendation-click", evalCtx,
        TrackingEventDetails.Builder().Set("item", click.Item).Build());

    return TypedResults.NoContent();
}).RequireAuthorization();

app.Run();

// The subject comes from the registered ISubjectProvider (JwtSubjectProvider), not from a raw claim:
// ASP.NET's JWT handler remaps the "sub" claim, so reading it directly would miss it.
static string ResolveSubject(IEnumerable<ISubjectProvider> providers, HttpContext httpContext)
{
    foreach (var provider in providers)
        if (provider.GetSubject(httpContext) is { } subject)
            return subject;

    return "anonymous";
}

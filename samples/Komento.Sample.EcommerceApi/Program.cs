using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Komento;
using Komento.AspNetCore;
using Komento.OpenFeature;
using Komento.Sample.EcommerceApi.Infrastructure;
using Komento.Sample.EcommerceApi.Komento;
using Komento.Sample.ServiceDefaults;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using OpenFeature;
using OpenFeature.Model;

const string JwtIssuer   = "komento-sample";
const string JwtAudience = "komento-sample";
const string JwtSecret   = "komento-sample-secret-key-must-be-at-least-32-chars!";

var builder = WebApplication.CreateBuilder(args);
builder.AddServiceDefaults();

// Aspire-managed infrastructure
builder.AddNatsClient("nats");
builder.AddNpgsqlDataSource("komento-db");

// Komento core
builder.Services.AddKomento()
.AddSource<NatsExperimentSource>()
.AddSegmentProvider<AppSegmentProvider>();

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

app.MapGet("/token", (string userId, string plan = "free") =>
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
        expires:            DateTime.UtcNow.AddHours(1),
        signingCredentials: credentials);

    return Results.Ok(new { token = new JwtSecurityTokenHandler().WriteToken(token) });
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

    var isPremium    = await client.GetBoolAsync  ("premium-product-page",      subjectId ?? "", ctx, defaultValue: false,     ct: ct);
    var priceVariant = await client.GetStringAsync("price-display",             subjectId ?? "", ctx, defaultValue: "default", ct: ct);

    return Results.Ok(new
    {
        productId    = id,
        name         = $"Komento Widget {id}",
        price        = priceVariant switch
        {
            "loyalty-price" => 79.99m,
            "vip-price"     => 89.99m,
            _               => 99.99m
        },
        premiumPage  = isPremium,
        priceVariant
    });
}).RequireAuthorization();

// ── /recommendations — uses OpenFeature IFeatureClient ────────────────────

app.MapGet("/recommendations", async (
    HttpContext httpContext,
    IFeatureClient featureClient,
    CancellationToken ct) =>
{
    var subjectId = httpContext.User.FindFirst("sub")?.Value ?? "anonymous";
    var plan      = httpContext.User.FindFirst("plan")?.Value ?? "free";

    var evalCtx = global::OpenFeature.Model.EvaluationContext.Builder()
        .SetTargetingKey(subjectId)
        .Set("plan", new Value(plan))
        .Build();

    var algo = await featureClient.GetStringValueAsync("recommendation-algorithm", "collaborative", evalCtx);

    return Results.Ok(new
    {
        algorithm = algo,
        items     = algo == "content-based"
            ? new[] { "Widget A", "Widget B", "Widget C" }
            : new[] { "Widget D", "Widget E", "Widget F" }
    });
}).RequireAuthorization();

app.Run();

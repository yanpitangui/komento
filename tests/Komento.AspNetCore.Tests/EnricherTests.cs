using System.Net;
using System.Net.Http.Headers;
using AwesomeAssertions;
using Komento;
using Komento.AspNetCore;
using Komento.Internals;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using TUnit.Core;

namespace Komento.AspNetCore.Tests;

public class EnricherTests
{
    // Experiment that requires country == "BR" to be eligible.
    private static ExperimentConfig CountryFilteredConfig() => new()
    {
        Id            = "country-flag",
        SubjectType   = "user",
        Variants      = [new VariantConfig { Name = "treatment", Allocation = 1.0 }],
        GlobalFilters = [new TraitEqualsFilter { Key = "country", Value = "BR" }]
    };

    // Enricher that reads X-Country header and sets it in the evaluation context.
    private sealed class CountryHeaderEnricher : IEvaluationContextEnricher
    {
        public ValueTask EnrichAsync(HttpContext context, EvaluationContextBuilder builder, CancellationToken ct)
        {
            var country = context.Request.Headers["X-Country"].FirstOrDefault();
            if (country is not null)
                builder.Set("country", country);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class TestApp : IAsyncDisposable
    {
        private readonly WebApplication _app;
        private TestApp(WebApplication app) => _app = app;

        public HttpClient CreateClient() => _app.GetTestClient();

        public static async Task<TestApp> CreateAsync()
        {
            var config  = CountryFilteredConfig();
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions
            {
                ContentRootPath = TestHelpers.ResolveContentRoot()
            });
            builder.WebHost.UseTestServer();

            var options = new KomentoOptions { Experiments = new HashSet<string> { config.Id } };
            var engine  = new ExperimentClient(options);
            await engine.UpdateAsync(new Dictionary<string, ExperimentConfig> { [config.Id] = config });

            builder.Services.AddSingleton<IExperimentClient>(engine);
            builder.Services.AddSingleton<IConfigUpdater>(engine);
            builder.Services.AddSingleton<ISubjectProvider>(new FixedSubjectProvider("user-1"));
            builder.Services.AddKomentoAspNetCore()
                   .AddEnricher<CountryHeaderEnricher>();

            var app = builder.Build();
            app.MapGet("/gate", () => "ok").RequireVariant("country-flag", "treatment");
            await app.StartAsync();
            return new TestApp(app);
        }

        public async ValueTask DisposeAsync() => await _app.StopAsync();
    }

    [Test]
    public async Task Enricher_passes_context_that_satisfies_filter()
    {
        await using var app    = await TestApp.CreateAsync();
        var client             = app.CreateClient();
        client.DefaultRequestHeaders.Add("X-Country", "BR");

        var resp = await client.GetAsync("/gate");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Test]
    public async Task Enricher_absent_context_fails_filter_and_blocks()
    {
        await using var app = await TestApp.CreateAsync();
        var resp = await app.CreateClient().GetAsync("/gate");
        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Test]
    public async Task Enricher_wrong_country_fails_filter_and_blocks()
    {
        await using var app    = await TestApp.CreateAsync();
        var client             = app.CreateClient();
        client.DefaultRequestHeaders.Add("X-Country", "US");

        var resp = await client.GetAsync("/gate");
        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    private sealed class FixedSubjectProvider(string subjectId) : ISubjectProvider
    {
        public string SubjectType => "user";
        public string? GetSubject(HttpContext context) => subjectId;
    }
}

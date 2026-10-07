using System.Net;
using AwesomeAssertions;
using Komento;
using Komento.AspNetCore;
using Komento.OpenFeature;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using TUnit.Core;

namespace Komento.OpenFeature.AspNetCore.Tests;

public class TransactionContextTests
{
    private sealed class HeaderSubjectProvider : ISubjectProvider
    {
        public string  SubjectType => "user";
        public string? GetSubject(HttpContext context) => context.Request.Headers["x-user"].FirstOrDefault();
    }

    private sealed class PlanEnricher : IEvaluationContextEnricher
    {
        public ValueTask EnrichAsync(HttpContext context, EvaluationContextBuilder builder, CancellationToken ct = default)
        {
            if (context.Request.Headers["x-plan"].FirstOrDefault() is { } plan) builder.Set("plan", plan);
            return ValueTask.CompletedTask;
        }
    }

    /// <summary>An app whose only endpoint evaluates a flag through OpenFeature, passing NO context.</summary>
    private static async Task<WebApplication> StartAsync()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { ContentRootPath = TestHelpers.ResolveContentRoot() });
        builder.WebHost.UseTestServer();

        builder.Services.AddKomento();
        builder.Services.AddKomentoAspNetCore()
            .AddSubjectProvider<HeaderSubjectProvider>()
            .AddEnricher<PlanEnricher>();

        var app = builder.Build();
        await app.Services.GetRequiredService<IConfigUpdater>().UpdateAsync(new ExperimentConfig
        {
            Id            = "premium-flag",
            SubjectType   = "user",
            Variants      = [new VariantConfig { Name = "on", Allocation = 1.0, Value = true }],
            GlobalFilters = [new TraitEqualsFilter { Key = "plan", Value = "premium" }]
        });

        var domain = $"ctx-{Guid.NewGuid():N}";
        await global::OpenFeature.Api.Instance.SetProviderAsync(
            domain, new KomentoFeatureProvider(app.Services.GetRequiredService<IExperimentClient>()));
        var featureClient = global::OpenFeature.Api.Instance.GetClient(domain);

        app.UseKomentoOpenFeatureContext();
        app.MapGet("/flag", async () => (await featureClient.GetBooleanValueAsync("premium-flag", false)).ToString());
        await app.StartAsync();
        return app;
    }

    private static async Task<string> GetAsync(HttpClient http, string user, string plan)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/flag");
        request.Headers.Add("x-user", user);
        request.Headers.Add("x-plan", plan);
        return await (await http.SendAsync(request)).Content.ReadAsStringAsync();
    }

    [Test]
    public async Task An_OpenFeature_evaluation_sees_the_subject_and_attributes_the_enrichers_built()
    {
        await using var app = await StartAsync();
        var http = app.GetTestClient();

        (await GetAsync(http, "alice", "premium")).Should().Be("True");   // plan=premium satisfies the filter
        (await GetAsync(http, "bob", "free")).Should().Be("False");       // plan=free does not
    }

    [Test]
    public async Task Concurrent_requests_keep_their_own_context()
    {
        await using var app = await StartAsync();
        var http = app.GetTestClient();

        var results = await Task.WhenAll(Enumerable.Range(0, 40).Select(async i =>
        {
            var plan = i % 2 == 0 ? "premium" : "free";
            return (Plan: plan, Result: await GetAsync(http, $"user-{i}", plan));
        }));

        results.Should().OnlyContain(r => r.Result == (r.Plan == "premium" ? "True" : "False"));
    }

    [Test]
    public async Task Requests_without_a_resolvable_subject_pass_through_unchanged()
    {
        await using var app = await StartAsync();

        var response = await app.GetTestClient().GetAsync("/flag");

        response.StatusCode.Should().Be(HttpStatusCode.OK);   // the middleware does not reject the request
    }
}

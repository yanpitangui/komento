using System.Net;
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

public class StaticContextTests
{
    private sealed class FixedSubjectProvider(string subjectId) : ISubjectProvider
    {
        public string  SubjectType => "user";
        public string? GetSubject(HttpContext context) => subjectId;
    }

    private static readonly ExperimentConfig EuOnly = new()
    {
        Id            = "region-flag",
        SubjectType   = "user",
        Variants      = [new VariantConfig { Name = "on", Allocation = 1.0 }],
        GlobalFilters = [new TraitEqualsFilter { Key = "region", Value = "eu" }]
    };

    private static async Task<HttpStatusCode> GetAsync(EvaluationContext staticContext)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ContentRootPath = TestHelpers.ResolveContentRoot()
        });
        builder.WebHost.UseTestServer();

        var engine = new ExperimentClient(new KomentoOptions { StaticContext = staticContext });
        await engine.UpdateAsync(EuOnly);

        builder.Services.AddSingleton<IExperimentClient>(engine);
        builder.Services.AddSingleton<IConfigUpdater>(engine);
        builder.Services.AddSingleton<ISubjectProvider>(new FixedSubjectProvider("user-1"));
        builder.Services.AddKomentoAspNetCore();

        var app = builder.Build();
        app.MapGet("/gated", () => "ok").RequireVariant("region-flag", "on");
        await app.StartAsync();
        try
        {
            return (await app.GetTestClient().GetAsync("/gated")).StatusCode;
        }
        finally
        {
            await app.StopAsync();
        }
    }

    [Test]
    public async Task Static_context_satisfies_the_experiments_filter_for_a_gated_endpoint()
    {
        var status = await GetAsync(EvaluationContext.Create().Set("region", "eu").Build());

        status.Should().Be(HttpStatusCode.OK);
    }

    [Test]
    public async Task Without_the_static_attribute_the_gated_endpoint_is_not_found()
    {
        var status = await GetAsync(EvaluationContext.Empty);

        status.Should().Be(HttpStatusCode.NotFound);
    }
}

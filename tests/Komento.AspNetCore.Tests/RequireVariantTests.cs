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

public class RequireVariantTests
{
    private static ExperimentConfig FullAlloc(string variant) => new()
    {
        Id          = "test-flag",
        SubjectType = "user",
        Variants    = [new VariantConfig { Name = variant, Allocation = 1.0 }]
    };

    private sealed class TestApp : IAsyncDisposable
    {
        private readonly WebApplication _app;

        private TestApp(WebApplication app) => _app = app;

        public HttpClient CreateClient() => _app.GetTestClient();

        public static async Task<TestApp> CreateAsync(
            ExperimentConfig config,
            string requiredVariant,
            string? subjectId = null)
        {
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions
            {
                ContentRootPath = TestHelpers.ResolveContentRoot()
            });
            builder.WebHost.UseTestServer();

            var engine = new ExperimentClient(new KomentoOptions());
            await engine.UpdateAsync(new Dictionary<string, ExperimentConfig> { [config.Id] = config });

            builder.Services.AddSingleton<IExperimentClient>(engine);
            builder.Services.AddSingleton<IConfigUpdater>(engine);
            if (subjectId is not null)
                builder.Services.AddSingleton<ISubjectProvider>(new FixedSubjectProvider(subjectId));
            builder.Services.AddKomentoAspNetCore();

            var app = builder.Build();
            app.MapGet("/minimal", () => "ok").RequireVariant("test-flag", requiredVariant);
            await app.StartAsync();
            return new TestApp(app);
        }

        public async ValueTask DisposeAsync() => await _app.StopAsync();
    }

    [Test]
    public async Task Endpoint_filter_allows_when_variant_matches()
    {
        await using var app = await TestApp.CreateAsync(FullAlloc("treatment"), "treatment", "user-1");
        var resp = await app.CreateClient().GetAsync("/minimal");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Test]
    public async Task Endpoint_filter_blocks_when_variant_does_not_match()
    {
        await using var app = await TestApp.CreateAsync(FullAlloc("control"), "treatment", "user-1");
        var resp = await app.CreateClient().GetAsync("/minimal");
        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Test]
    public async Task Endpoint_filter_returns_404_when_no_subject_provider()
    {
        await using var app = await TestApp.CreateAsync(FullAlloc("treatment"), "treatment");
        var resp = await app.CreateClient().GetAsync("/minimal");
        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    private sealed class FixedSubjectProvider(string subjectId) : ISubjectProvider
    {
        public string SubjectType => "user";
        public string? GetSubject(HttpContext context) => subjectId;
    }
}

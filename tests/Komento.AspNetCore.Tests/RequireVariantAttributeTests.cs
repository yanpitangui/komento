using System.Net;
using AwesomeAssertions;
using Komento;
using Komento.AspNetCore;
using Komento.Internals;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using TUnit.Core;

namespace Komento.AspNetCore.Tests;

// Must be a top-level public class — ControllerFeatureProvider checks IsPublic, which is false for nested types.
[ApiController, Route("mvc")]
public class GateMvcController : ControllerBase
{
    [HttpGet("gate"), RequireVariant("test-flag", "treatment")]
    public IActionResult Gate() => Ok("ok");

    [HttpGet("ping")]
    public IActionResult Ping() => Ok("pong");
}

public class RequireVariantAttributeTests
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
            builder.Services.AddControllers()
                   .AddApplicationPart(typeof(GateMvcController).Assembly);

            var app = builder.Build();
            app.MapControllers();
            await app.StartAsync();
            return new TestApp(app);
        }

        public async ValueTask DisposeAsync() => await _app.StopAsync();
    }

    [Test]
    public async Task Action_filter_allows_when_variant_matches()
    {
        await using var app = await TestApp.CreateAsync(FullAlloc("treatment"), "user-1");
        var resp = await app.CreateClient().GetAsync("/mvc/gate");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Test]
    public async Task Action_filter_blocks_when_variant_does_not_match()
    {
        await using var app = await TestApp.CreateAsync(FullAlloc("control"), "user-1");
        var resp = await app.CreateClient().GetAsync("/mvc/gate");
        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Test]
    public async Task Action_filter_returns_404_when_no_subject_provider()
    {
        await using var app = await TestApp.CreateAsync(FullAlloc("treatment"));
        var resp = await app.CreateClient().GetAsync("/mvc/gate");
        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    private sealed class FixedSubjectProvider(string subjectId) : ISubjectProvider
    {
        public string SubjectType => "user";
        public string? GetSubject(HttpContext context) => subjectId;
    }
}

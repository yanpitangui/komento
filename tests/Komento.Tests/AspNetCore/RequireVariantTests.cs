using System.Net;
using Komento;
using Komento.AspNetCore;
using Komento.Internals;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Komento.Tests.AspNetCore;

public class RequireVariantTests
{
    private static ExperimentConfig FullAlloc(string variant) => new()
    {
        Id          = "test-flag",
        SubjectType = "user",
        Variants    = [new VariantConfig { Name = variant, Allocation = 1.0 }]
    };

    private static async Task<(WebApplication App, HttpClient Client)> BuildAsync(
        string configVariant, string requiredVariant, string subjectId)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();

        var config  = FullAlloc(configVariant);
        var options = new KomentoOptions { Experiments = new HashSet<string> { config.Id } };
        var engine  = new ExperimentClient(options);
        await engine.UpdateAsync(new Dictionary<string, ExperimentConfig> { [config.Id] = config });

        builder.Services.AddSingleton<IExperimentClient>(engine);
        builder.Services.AddSingleton<IConfigUpdater>(engine);
        builder.Services.AddSingleton<ISubjectProvider>(new FixedSubjectProvider(subjectId));
        builder.Services.AddKomentoAspNetCore();

        var app = builder.Build();
        app.MapGet("/minimal", () => "ok").RequireVariant("test-flag", requiredVariant);

        await app.StartAsync();
        return (app, app.GetTestClient());
    }

    [Fact]
    public async Task Endpoint_filter_allows_when_variant_matches()
    {
        var (app, client) = await BuildAsync(configVariant: "treatment", requiredVariant: "treatment", subjectId: "user-1");
        try
        {
            var resp = await client.GetAsync("/minimal");
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        }
        finally { await app.StopAsync(); }
    }

    [Fact]
    public async Task Endpoint_filter_blocks_when_variant_does_not_match()
    {
        // Everyone gets "control" but endpoint requires "treatment"
        var (app, client) = await BuildAsync(configVariant: "control", requiredVariant: "treatment", subjectId: "user-1");
        try
        {
            var resp = await client.GetAsync("/minimal");
            Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
        }
        finally { await app.StopAsync(); }
    }

    [Fact]
    public async Task Endpoint_filter_returns_404_when_no_subject_provider()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();

        var config  = FullAlloc("treatment");
        var options = new KomentoOptions { Experiments = new HashSet<string> { config.Id } };
        var engine  = new ExperimentClient(options);
        await engine.UpdateAsync(new Dictionary<string, ExperimentConfig> { [config.Id] = config });

        builder.Services.AddSingleton<IExperimentClient>(engine);
        builder.Services.AddKomentoAspNetCore(); // no subject provider

        var app = builder.Build();
        app.MapGet("/minimal", () => "ok").RequireVariant("test-flag", "treatment");

        await app.StartAsync();
        try
        {
            var resp = await app.GetTestClient().GetAsync("/minimal");
            Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
        }
        finally { await app.StopAsync(); }
    }

    private sealed class FixedSubjectProvider : ISubjectProvider
    {
        private readonly string _subjectId;
        public string SubjectType => "user";
        public FixedSubjectProvider(string subjectId) => _subjectId = subjectId;
        public string? GetSubject(HttpContext context) => _subjectId;
    }
}

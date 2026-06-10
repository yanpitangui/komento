using AwesomeAssertions;
using Komento;
using Komento.Http;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using TUnit.Core;

namespace Komento.Http.Tests;

public class HttpExperimentSourceIntegrationTests
{
    [Test]
    public async Task LoadAsync_fetches_and_deserializes_through_real_http_pipeline()
    {
        var payload = new[]
        {
            new { id = "checkout-flow", subjectType = "user" },
            new { id = "dark-mode",     subjectType = "user" }
        };

        using var host = await BuildTestServerAsync(payload);

        var source = BuildSource<List<FlagDto>>(host,
            dtos => dtos.Select(d => new ExperimentConfig
            {
                Id          = d.Id,
                SubjectType = d.SubjectType,
                Variants    = [new VariantConfig { Name = "control", Allocation = 1.0 }]
            }));

        var result = await source.LoadAsync(new HashSet<string>());

        result.Keys.Should().BeEquivalentTo(["checkout-flow", "dark-mode"]);
    }

    [Test]
    public async Task LoadAsync_applies_experimentIds_filter_after_fetch()
    {
        var payload = new[]
        {
            new { id = "checkout-flow", subjectType = "user" },
            new { id = "dark-mode",     subjectType = "user" }
        };

        using var host = await BuildTestServerAsync(payload);

        var source = BuildSource<List<FlagDto>>(host,
            dtos => dtos.Select(d => new ExperimentConfig
            {
                Id          = d.Id,
                SubjectType = d.SubjectType,
                Variants    = [new VariantConfig { Name = "control", Allocation = 1.0 }]
            }));

        var result = await source.LoadAsync(new HashSet<string> { "checkout-flow" });

        result.Keys.Should().BeEquivalentTo(["checkout-flow"]);
    }

    [Test]
    public async Task LoadAsync_supports_nested_response_shape_through_real_pipeline()
    {
        var payload = new { experiments = new[] { new { id = "exp-1", subjectType = "user" } } };

        using var host = await BuildTestServerAsync(payload);

        var source = BuildSource<ApiResponse>(host,
            r => r.Experiments.Select(d => new ExperimentConfig
            {
                Id          = d.Id,
                SubjectType = d.SubjectType,
                Variants    = [new VariantConfig { Name = "control", Allocation = 1.0 }]
            }));

        var result = await source.LoadAsync(new HashSet<string>());

        result.Keys.Should().BeEquivalentTo(["exp-1"]);
    }

    private static Task<IHost> BuildTestServerAsync(object responseBody)
        => new HostBuilder()
            .ConfigureWebHost(web =>
            {
                web.UseTestServer();
                web.Configure(app =>
                    app.Run(ctx => ctx.Response.WriteAsJsonAsync(responseBody)));
            })
            .StartAsync();

    private static IExperimentSource BuildSource<TResponse>(
        IHost testHost,
        Func<TResponse, IEnumerable<ExperimentConfig>> map)
    {
        var services = new ServiceCollection();
        services.AddKomento()
                .AddHttpSource(map);

        services.AddHttpClient("Komento.Http",
                    c => c.BaseAddress = new Uri("http://localhost/"))
                .ConfigurePrimaryHttpMessageHandler(testHost.GetTestServer().CreateHandler);

        return services.BuildServiceProvider().GetRequiredService<IExperimentSource>();
    }

    private record FlagDto(string Id, string SubjectType);
    private record FlagItemDto(string Id, string SubjectType);
    private record ApiResponse(List<FlagItemDto> Experiments);
}

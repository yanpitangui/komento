using System.Net;
using System.Text.Json;
using AwesomeAssertions;
using Komento;
using Komento.Http;
using Microsoft.Extensions.DependencyInjection;
using TUnit.Core;

namespace Komento.Http.Tests;

public class HttpExperimentSourceTests
{
    [Test]
    public async Task LoadAsync_maps_response_to_experiment_configs()
    {
        var provider = BuildProvider(
            """[{"id":"checkout-flow","subjectType":"user"},{"id":"dark-mode","subjectType":"user"}]""",
            (List<FlagDto> dtos) => dtos.Select(d => new ExperimentConfig
            {
                Id          = d.Id,
                SubjectType = d.SubjectType,
                Variants    = [new VariantConfig { Name = "control", Allocation = 1.0 }]
            }));

        var source = provider.GetRequiredService<IExperimentSource>();
        var result = await source.LoadAsync(new HashSet<string>());

        result.Keys.Should().BeEquivalentTo(["checkout-flow", "dark-mode"]);
    }

    [Test]
    public async Task LoadAsync_filters_by_experimentIds_when_non_empty()
    {
        var provider = BuildProvider(
            """[{"id":"checkout-flow","subjectType":"user"},{"id":"dark-mode","subjectType":"user"}]""",
            (List<FlagDto> dtos) => dtos.Select(d => new ExperimentConfig
            {
                Id          = d.Id,
                SubjectType = d.SubjectType,
                Variants    = [new VariantConfig { Name = "control", Allocation = 1.0 }]
            }));

        var source = provider.GetRequiredService<IExperimentSource>();
        var result = await source.LoadAsync(new HashSet<string> { "checkout-flow" });

        result.Keys.Should().BeEquivalentTo(["checkout-flow"]);
        result.ContainsKey("dark-mode").Should().BeFalse();
    }

    [Test]
    public void Constructor_throws_when_BaseAddress_not_configured()
    {
        var services = new ServiceCollection();
        services.AddKomento()
                .AddHttpSource((List<FlagDto> dtos) => dtos.Select(d =>
                    new ExperimentConfig { Id = d.Id, SubjectType = "user", Variants = [] }));

        var provider = services.BuildServiceProvider();
        var act = () => provider.GetRequiredService<IExperimentSource>();

        act.Should().Throw<InvalidOperationException>()
           .WithMessage("*BaseAddress*");
    }

    [Test]
    public async Task LoadAsync_supports_nested_response_shape()
    {
        var provider = BuildProvider(
            """{"experiments":[{"id":"exp-1","subjectType":"user"}]}""",
            (ApiResponse r) => r.Experiments.Select(d => new ExperimentConfig
            {
                Id          = d.Id,
                SubjectType = d.SubjectType,
                Variants    = [new VariantConfig { Name = "control", Allocation = 1.0 }]
            }));

        var source = provider.GetRequiredService<IExperimentSource>();
        var result = await source.LoadAsync(new HashSet<string>());

        result.Keys.Should().BeEquivalentTo(["exp-1"]);
    }

    private static ServiceProvider BuildProvider<TResponse>(
        string json,
        Func<TResponse, IEnumerable<ExperimentConfig>> map)
    {
        var services = new ServiceCollection();
        services.AddKomento()
                .AddHttpSource(map);

        services.AddHttpClient("Komento.Http",
                c => c.BaseAddress = new Uri("https://flags.test/experiments"))
                .ConfigurePrimaryHttpMessageHandler(() => new StubHandler(json));

        return services.BuildServiceProvider();
    }

    private sealed class StubHandler(string json) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
            });
    }

    private record FlagDto(string Id, string SubjectType);

    private record ApiResponse(List<FlagDto> Experiments);
}

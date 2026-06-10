using AwesomeAssertions;
using Komento;
using Microsoft.Extensions.DependencyInjection;
using TUnit.Core;

namespace Komento.Tests;

public class DependencyInjectionTests
{
    [Test]
    public void AddKomento_registers_IExperimentClient()
    {
        var services = new ServiceCollection();
        services.AddKomento();

        var provider = services.BuildServiceProvider();
        provider.GetService<IExperimentClient>().Should().NotBeNull();
    }

    [Test]
    public void AddKomento_registers_IConfigUpdater()
    {
        var services = new ServiceCollection();
        services.AddKomento();

        var provider = services.BuildServiceProvider();
        provider.GetService<IConfigUpdater>().Should().NotBeNull();
    }

    [Test]
    public void IExperimentClient_and_IConfigUpdater_are_same_instance()
    {
        var services = new ServiceCollection();
        services.AddKomento();

        var provider = services.BuildServiceProvider();
        var client   = provider.GetRequiredService<IExperimentClient>();
        var updater  = provider.GetRequiredService<IConfigUpdater>();

        updater.Should().BeSameAs(client);
    }

    [Test]
    public async Task InitializeKomentoAsync_loads_configs_from_source()
    {
        var services = new ServiceCollection();
        services.AddKomento()
                .AddSource<StubExperimentSource>();

        var provider = services.BuildServiceProvider();
        await provider.InitializeKomentoAsync();

        var client = provider.GetRequiredService<IExperimentClient>();
        var result = await client.GetVariantAsync("exp-1", "user-1", EvaluationContext.Empty);
        result.IsEligible.Should().BeTrue();
        result.VariantName.Should().Be("control");
    }

    [Test]
    public async Task InitializeKomentoAsync_is_noop_when_no_source_registered()
    {
        var services = new ServiceCollection();
        services.AddKomento();

        var provider = services.BuildServiceProvider();
        await provider.InitializeKomentoAsync();

        var client = provider.GetRequiredService<IExperimentClient>();
        var result = await client.GetVariantAsync("exp-1", "user-1", EvaluationContext.Empty);
        result.Should().Be(VariantResult.NotFound);
    }

    private sealed class StubExperimentSource : IExperimentSource
    {
        public ValueTask<IReadOnlyDictionary<string, ExperimentConfig>> LoadAsync(
            IReadOnlySet<string> experimentIds, CancellationToken ct = default)
        {
            IReadOnlyDictionary<string, ExperimentConfig> configs = new Dictionary<string, ExperimentConfig>(StringComparer.Ordinal)
            {
                ["exp-1"] = new ExperimentConfig
                {
                    Id          = "exp-1",
                    SubjectType = "user",
                    Variants    = [new VariantConfig { Name = "control", Allocation = 1.0 }]
                }
            };
            return ValueTask.FromResult(configs);
        }
    }
}

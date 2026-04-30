using Komento;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Komento.Tests;

public class DependencyInjectionTests
{
    [Fact]
    public void AddKomento_registers_IExperimentClient()
    {
        var services = new ServiceCollection();
        services.AddKomento(o => o.Experiments = new HashSet<string> { "exp-1" });

        var provider = services.BuildServiceProvider();
        Assert.NotNull(provider.GetService<IExperimentClient>());
    }

    [Fact]
    public void AddKomento_registers_IConfigUpdater()
    {
        var services = new ServiceCollection();
        services.AddKomento(o => o.Experiments = new HashSet<string> { "exp-1" });

        var provider = services.BuildServiceProvider();
        Assert.NotNull(provider.GetService<IConfigUpdater>());
    }

    [Fact]
    public void IExperimentClient_and_IConfigUpdater_are_same_instance()
    {
        var services = new ServiceCollection();
        services.AddKomento(o => o.Experiments = new HashSet<string> { "exp-1" });

        var provider = services.BuildServiceProvider();
        var client   = provider.GetRequiredService<IExperimentClient>();
        var updater  = provider.GetRequiredService<IConfigUpdater>();

        Assert.Same(client, updater);
    }

    [Fact]
    public async Task InitializeKomentoAsync_loads_configs_from_source()
    {
        var services = new ServiceCollection();
        services.AddKomento(o => o.Experiments = new HashSet<string> { "exp-1" })
                .AddSource<StubExperimentSource>();

        var provider = services.BuildServiceProvider();
        await provider.InitializeKomentoAsync();

        var client = provider.GetRequiredService<IExperimentClient>();
        var result = await client.GetVariantAsync("exp-1", "user-1", EvaluationContext.Empty);
        Assert.True(result.IsEligible);
        Assert.Equal("control", result.VariantName);
    }

    [Fact]
    public async Task InitializeKomentoAsync_is_noop_when_no_source_registered()
    {
        var services = new ServiceCollection();
        services.AddKomento(o => o.Experiments = new HashSet<string> { "exp-1" });

        var provider = services.BuildServiceProvider();
        // should not throw
        await provider.InitializeKomentoAsync();

        var client = provider.GetRequiredService<IExperimentClient>();
        var result = await client.GetVariantAsync("exp-1", "user-1", EvaluationContext.Empty);
        Assert.Equal(VariantResult.NotFound, result);
    }

    private sealed class StubExperimentSource : IExperimentSource
    {
        public ValueTask<IReadOnlyDictionary<string, ExperimentConfig>> LoadAsync(
            IReadOnlySet<string> experimentIds, CancellationToken ct = default)
        {
            var configs = new Dictionary<string, ExperimentConfig>(StringComparer.Ordinal);
            foreach (var id in experimentIds)
                configs[id] = new ExperimentConfig
                {
                    Id          = id,
                    SubjectType = "user",
                    Variants    = [new VariantConfig { Name = "control", Allocation = 1.0 }]
                };
            return ValueTask.FromResult<IReadOnlyDictionary<string, ExperimentConfig>>(configs);
        }
    }
}

using AwesomeAssertions;
using Komento;
using Komento.Internals;
using Komento.OpenFeature;
using OpenFeature.Constant;
using TUnit.Core;
using OFContext = OpenFeature.Model.EvaluationContext;

namespace Komento.OpenFeature.Tests;

public class LifecycleTests
{
    private sealed class StubSource(IReadOnlyDictionary<string, ExperimentConfig> configs) : IExperimentSource
    {
        public ValueTask<IReadOnlyDictionary<string, ExperimentConfig>> LoadAsync(
            IReadOnlySet<string> experimentIds, CancellationToken ct = default)
            => ValueTask.FromResult(configs);
    }

    private sealed class FailingSource : IExperimentSource
    {
        public ValueTask<IReadOnlyDictionary<string, ExperimentConfig>> LoadAsync(
            IReadOnlySet<string> experimentIds, CancellationToken ct = default)
            => throw new InvalidOperationException("source is down");
    }

    private static ExperimentConfig On(string id) => new()
    {
        Id          = id,
        SubjectType = "user",
        Variants    = [new VariantConfig { Name = "on", Allocation = 1.0, Value = true }]
    };

    private static OFContext Subject => OFContext.Builder().SetTargetingKey("user-1").Build();

    [Test]
    public async Task Initialize_loads_the_configs_so_the_first_evaluation_finds_them()
    {
        var engine   = new ExperimentClient(new KomentoOptions());
        var source   = new StubSource(new Dictionary<string, ExperimentConfig> { ["lifecycle-flag"] = On("lifecycle-flag") });
        var provider = new KomentoFeatureProvider(engine, source: source, updater: engine);
        var domain   = $"lifecycle-{Guid.NewGuid():N}";

        await global::OpenFeature.Api.Instance.SetProviderAsync(domain, provider);   // no InitializeKomentoAsync call

        var client = global::OpenFeature.Api.Instance.GetClient(domain);
        client.ProviderStatus.Should().Be(ProviderStatus.Ready);
        (await client.GetBooleanValueAsync("lifecycle-flag", false, Subject)).Should().BeTrue();
    }

    [Test]
    public async Task Without_a_source_the_provider_is_ready_immediately()
    {
        var engine   = new ExperimentClient(new KomentoOptions());
        var provider = new KomentoFeatureProvider(engine);
        var domain   = $"lifecycle-{Guid.NewGuid():N}";

        await global::OpenFeature.Api.Instance.SetProviderAsync(domain, provider);

        global::OpenFeature.Api.Instance.GetClient(domain).ProviderStatus.Should().Be(ProviderStatus.Ready);
    }

    [Test]
    public async Task A_failing_source_leaves_the_provider_in_an_error_status()
    {
        var engine   = new ExperimentClient(new KomentoOptions());
        var provider = new KomentoFeatureProvider(engine, source: new FailingSource(), updater: engine);
        var domain   = $"lifecycle-{Guid.NewGuid():N}";

        try { await global::OpenFeature.Api.Instance.SetProviderAsync(domain, provider); }
        catch (Exception) { /* whether the SDK rethrows is not part of the contract; the status below is */ }

        var client = global::OpenFeature.Api.Instance.GetClient(domain);
        client.ProviderStatus.Should().BeOneOf(ProviderStatus.Error, ProviderStatus.Fatal);
        (await client.GetBooleanValueAsync("anything", true, Subject)).Should().BeTrue();   // the caller's default
    }
}

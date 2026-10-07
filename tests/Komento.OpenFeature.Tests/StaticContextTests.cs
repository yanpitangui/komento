using AwesomeAssertions;
using Komento;
using Komento.Internals;
using Komento.OpenFeature;
using TUnit.Core;
using KomentoContext = Komento.EvaluationContext;
using OFContext = OpenFeature.Model.EvaluationContext;

namespace Komento.OpenFeature.Tests;

public class StaticContextTests
{
    private static async Task<KomentoFeatureProvider> BuildProviderAsync(KomentoContext staticContext)
    {
        var client = new ExperimentClient(new KomentoOptions { StaticContext = staticContext });
        await client.UpdateAsync(new ExperimentConfig
        {
            Id            = "region-flag",
            SubjectType   = "user",
            Variants      = [new VariantConfig { Name = "on", Allocation = 1.0, Value = true }],
            GlobalFilters = [new TraitEqualsFilter { Key = "region", Value = "eu" }]
        });
        return new KomentoFeatureProvider(client);
    }

    private static readonly OFContext Subject = OFContext.Builder().SetTargetingKey("user-1").Build();

    [Test]
    public async Task Static_context_satisfies_the_experiments_filter_through_the_provider()
    {
        var provider = await BuildProviderAsync(KomentoContext.Create().Set("region", "eu").Build());

        var result = await provider.ResolveBooleanValueAsync("region-flag", false, Subject);

        result.Value.Should().BeTrue();
    }

    [Test]
    public async Task Without_the_static_attribute_the_provider_returns_the_default()
    {
        var provider = await BuildProviderAsync(KomentoContext.Empty);

        var result = await provider.ResolveBooleanValueAsync("region-flag", false, Subject);

        result.Value.Should().BeFalse();
    }
}

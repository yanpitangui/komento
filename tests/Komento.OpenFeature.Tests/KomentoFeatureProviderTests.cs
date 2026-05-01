using AwesomeAssertions;
using Komento;
using Komento.Internals;
using Komento.OpenFeature;
using OpenFeature.Constant;
using OpenFeature.Model;
using Xunit;
using OFContext = OpenFeature.Model.EvaluationContext;

namespace Komento.OpenFeature.Tests;

public class KomentoFeatureProviderTests
{
    // ── helpers ──────────────────────────────────────────────────────────────

    private static IExperimentClient BuildClient(ExperimentConfig? config = null)
    {
        var cfg = config ?? FiftyFifty();
        var options = new KomentoOptions { Experiments = new HashSet<string> { cfg.Id } };
        var client = new ExperimentClient(options);
        client.UpdateAsync(new Dictionary<string, ExperimentConfig> { [cfg.Id] = cfg }).AsTask().Wait();
        return client;
    }

    private static ExperimentConfig FiftyFifty(string id = "bool-flag") => new()
    {
        Id          = id,
        SubjectType = "user",
        Variants    =
        [
            new VariantConfig { Name = "control",   Allocation = 0.0 },
            new VariantConfig { Name = "treatment",  Allocation = 1.0, Value = true }
        ]
    };

    private static OFContext CtxFor(string subjectId, string? subjectType = null)
    {
        var builder = OFContext.Builder().SetTargetingKey(subjectId);
        if (subjectType is not null)
            builder = builder.Set("subjectType", new Value(subjectType));
        return builder.Build();
    }

    // ── metadata ─────────────────────────────────────────────────────────────

    [Fact]
    public void GetMetadata_returns_Komento()
    {
        var provider = new KomentoFeatureProvider(BuildClient());
        provider.GetMetadata().Name.Should().Be("Komento");
    }

    // ── targeting key missing ─────────────────────────────────────────────────

    [Fact]
    public async Task ResolveBooleanValue_no_targeting_key_returns_TargetingKeyMissing()
    {
        var provider = new KomentoFeatureProvider(BuildClient());
        var ctx = OFContext.Builder().Build(); // no targeting key

        var result = await provider.ResolveBooleanValueAsync("bool-flag", false, ctx);

        result.Value.Should().BeFalse();
        result.ErrorType.Should().Be(ErrorType.TargetingKeyMissing);
        result.Reason.Should().Be(Reason.Error);
    }

    [Fact]
    public async Task ResolveBooleanValue_null_context_returns_TargetingKeyMissing()
    {
        var provider = new KomentoFeatureProvider(BuildClient());

        var result = await provider.ResolveBooleanValueAsync("bool-flag", false, context: null);

        result.ErrorType.Should().Be(ErrorType.TargetingKeyMissing);
    }
}

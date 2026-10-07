using AwesomeAssertions;
using Komento;
using Komento.Internals;
using Komento.OpenFeature;
using OpenFeature.Constant;
using TUnit.Core;
using OFContext = OpenFeature.Model.EvaluationContext;

namespace Komento.OpenFeature.Tests;

public class ResolutionDetailsTests
{
    private static OFContext Subject(string id = "user-1") => OFContext.Builder().SetTargetingKey(id).Build();

    private static async Task<KomentoFeatureProvider> ProviderFor(ExperimentConfig config)
    {
        var engine = new ExperimentClient(new KomentoOptions());
        await engine.UpdateAsync(config);
        return new KomentoFeatureProvider(engine);
    }

    // value: null means the default payload (true).
    private static ExperimentConfig Config(
        object? value = null,
        double allocation = 1.0,
        IReadOnlyList<FilterConfig>? filters = null,
        IReadOnlyList<OverrideRule>? overrides = null) => new()
    {
        Id            = "details-flag",
        SubjectType   = "device",
        Variants      =
        [
            new VariantConfig { Name = "control", Allocation = 1.0 - allocation },
            new VariantConfig { Name = "on",      Allocation = allocation, Value = value ?? true }
        ],
        GlobalFilters = filters   ?? [],
        Overrides     = overrides ?? []
    };

    [Test]
    public async Task Hash_assignment_resolves_with_the_split_reason_variant_and_metadata()
    {
        var provider = await ProviderFor(Config());

        var result = await provider.ResolveBooleanValueAsync("details-flag", false, Subject());

        result.Value.Should().BeTrue();
        result.Reason.Should().Be(Reason.Split);
        result.Variant.Should().Be("on");
        result.FlagMetadata!.GetString("outcome").Should().Be("assigned");
        result.FlagMetadata.GetString("subjectType").Should().Be("device");
    }

    [Test]
    public async Task An_override_resolves_with_the_targeting_match_reason()
    {
        var provider = await ProviderFor(Config(
            allocation: 0.0, overrides: [new SubjectOverride { SubjectId = "vip", Variant = "on" }]));

        var result = await provider.ResolveBooleanValueAsync("details-flag", false, Subject("vip"));

        result.Value.Should().BeTrue();
        result.Reason.Should().Be(Reason.TargetingMatch);
    }

    [Test]
    public async Task An_ineligible_subject_gets_the_default_with_ineligible_metadata()
    {
        var provider = await ProviderFor(Config(filters: [new TraitEqualsFilter { Key = "region", Value = "eu" }]));

        var result = await provider.ResolveBooleanValueAsync("details-flag", false, Subject());

        result.Value.Should().BeFalse();
        result.Reason.Should().Be(Reason.Default);
        result.FlagMetadata!.GetString("outcome").Should().Be("ineligible");
        result.FlagMetadata.GetString("subjectType").Should().Be("device");
    }

    [Test]
    public async Task An_outsider_gets_the_default_with_outsider_metadata()
    {
        // A single variant with no allocation: every subject falls outside it.
        var provider = await ProviderFor(new ExperimentConfig
        {
            Id          = "details-flag",
            SubjectType = "device",
            Variants    = [new VariantConfig { Name = "on", Allocation = 0.0, Value = true }]
        });

        var result = await provider.ResolveBooleanValueAsync("details-flag", false, Subject());

        result.Reason.Should().Be(Reason.Default);
        result.FlagMetadata!.GetString("outcome").Should().Be("outsider");
    }

    [Test]
    public async Task A_value_of_the_wrong_type_is_a_type_mismatch_with_a_message()
    {
        var provider = await ProviderFor(Config(value: "not-a-bool"));

        var result = await provider.ResolveBooleanValueAsync("details-flag", false, Subject());

        result.Value.Should().BeFalse();
        result.ErrorType.Should().Be(ErrorType.TypeMismatch);
        result.Reason.Should().Be(Reason.Error);
        result.ErrorMessage.Should().Contain("on").And.Contain("String").And.Contain("Boolean");
    }

    [Test]
    public async Task An_unknown_flag_and_a_missing_targeting_key_carry_messages()
    {
        var provider = await ProviderFor(Config());

        var unknown = await provider.ResolveBooleanValueAsync("nope", false, Subject());
        var noKey   = await provider.ResolveBooleanValueAsync("details-flag", false, OFContext.Builder().Build());

        unknown.ErrorType.Should().Be(ErrorType.FlagNotFound);
        unknown.ErrorMessage.Should().Contain("nope");
        noKey.ErrorType.Should().Be(ErrorType.TargetingKeyMissing);
        noKey.ErrorMessage.Should().NotBeNullOrEmpty();
    }
}

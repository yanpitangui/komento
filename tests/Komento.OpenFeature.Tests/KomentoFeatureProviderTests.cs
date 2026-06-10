using AwesomeAssertions;
using Komento;
using Komento.Internals;
using Komento.OpenFeature;
using OpenFeature.Constant;
using OpenFeature.Model;
using TUnit.Core;
using OFContext = OpenFeature.Model.EvaluationContext;

namespace Komento.OpenFeature.Tests;

public class KomentoFeatureProviderTests
{
    // ── helpers ──────────────────────────────────────────────────────────────

    private static IExperimentClient BuildClient(ExperimentConfig? config = null)
    {
        var cfg = config ?? FiftyFifty();
        var client = new ExperimentClient(new KomentoOptions());
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

    [Test]
    public void GetMetadata_returns_Komento()
    {
        var provider = new KomentoFeatureProvider(BuildClient());
        provider.GetMetadata().Name.Should().Be("Komento");
    }

    // ── targeting key missing ─────────────────────────────────────────────────

    [Test]
    public async Task ResolveBooleanValue_no_targeting_key_returns_TargetingKeyMissing()
    {
        var provider = new KomentoFeatureProvider(BuildClient());
        var ctx = OFContext.Builder().Build(); // no targeting key

        var result = await provider.ResolveBooleanValueAsync("bool-flag", false, ctx);

        result.Value.Should().BeFalse();
        result.ErrorType.Should().Be(ErrorType.TargetingKeyMissing);
        result.Reason.Should().Be(Reason.Error);
    }

    [Test]
    public async Task ResolveBooleanValue_null_context_returns_TargetingKeyMissing()
    {
        var provider = new KomentoFeatureProvider(BuildClient());

        var result = await provider.ResolveBooleanValueAsync("bool-flag", false, context: null);

        result.ErrorType.Should().Be(ErrorType.TargetingKeyMissing);
    }

    [Test]
    public async Task ResolveBooleanValue_empty_targeting_key_returns_TargetingKeyMissing()
    {
        var provider = new KomentoFeatureProvider(BuildClient());
        var ctx = OFContext.Builder().SetTargetingKey(string.Empty).Build();

        var result = await provider.ResolveBooleanValueAsync("bool-flag", false, ctx);

        result.ErrorType.Should().Be(ErrorType.TargetingKeyMissing);
        result.Reason.Should().Be(Reason.Error);
    }

    // ── flag not found ────────────────────────────────────────────────────────

    [Test]
    public async Task ResolveBooleanValue_unknown_flag_returns_FlagNotFound()
    {
        var provider = new KomentoFeatureProvider(BuildClient());
        var ctx = CtxFor("user-1");

        var result = await provider.ResolveBooleanValueAsync("does-not-exist", false, ctx);

        result.Value.Should().BeFalse();
        result.ErrorType.Should().Be(ErrorType.FlagNotFound);
        result.Reason.Should().Be(Reason.Default);
    }

    // ── ineligible ────────────────────────────────────────────────────────────

    [Test]
    public async Task ResolveBooleanValue_ineligible_subject_returns_default_value()
    {
        var config = new ExperimentConfig
        {
            Id            = "gated-flag",
            SubjectType   = "user",
            Variants      = [new VariantConfig { Name = "treatment", Allocation = 1.0, Value = true }],
            GlobalFilters = [new TraitEqualsFilter { Key = "role", Value = "admin" }]
        };
        var provider = new KomentoFeatureProvider(BuildClient(config));
        var ctx = CtxFor("regular-user");

        var result = await provider.ResolveBooleanValueAsync("gated-flag", false, ctx);

        result.Value.Should().BeFalse();
        result.ErrorType.Should().Be(ErrorType.None);
        result.Reason.Should().Be(Reason.Default);
        result.Variant.Should().Be("control");
    }

    // ── outsider ──────────────────────────────────────────────────────────────

    [Test]
    public async Task ResolveBooleanValue_outsider_returns_default_value()
    {
        var config = new ExperimentConfig
        {
            Id          = "zero-flag",
            SubjectType = "user",
            Variants    = [new VariantConfig { Name = "treatment", Allocation = 0.0, Value = true }]
        };
        var provider = new KomentoFeatureProvider(BuildClient(config));
        var ctx = CtxFor("user-1");

        var result = await provider.ResolveBooleanValueAsync("zero-flag", false, ctx);

        result.Value.Should().BeFalse();
        result.ErrorType.Should().Be(ErrorType.None);
        result.Reason.Should().Be(Reason.Default);
    }

    // ── bool ──────────────────────────────────────────────────────────────────

    [Test]
    public async Task ResolveBooleanValue_returns_variant_value_on_match()
    {
        var provider = new KomentoFeatureProvider(BuildClient());
        var ctx = CtxFor("user-1");

        var result = await provider.ResolveBooleanValueAsync("bool-flag", false, ctx);

        result.Value.Should().BeTrue();
        result.Reason.Should().Be(Reason.TargetingMatch);
        result.Variant.Should().Be("treatment");
        result.ErrorType.Should().Be(ErrorType.None);
    }

    [Test]
    public async Task ResolveBooleanValue_parse_error_when_value_is_not_bool()
    {
        var config = new ExperimentConfig
        {
            Id          = "string-as-bool",
            SubjectType = "user",
            Variants    = [new VariantConfig { Name = "treatment", Allocation = 1.0, Value = "yes" }]
        };
        var provider = new KomentoFeatureProvider(BuildClient(config));
        var ctx = CtxFor("user-1");

        var result = await provider.ResolveBooleanValueAsync("string-as-bool", false, ctx);

        result.Value.Should().BeFalse();
        result.ErrorType.Should().Be(ErrorType.ParseError);
        result.Reason.Should().Be(Reason.Error);
    }

    // ── string ────────────────────────────────────────────────────────────────

    [Test]
    public async Task ResolveStringValue_returns_variant_value_on_match()
    {
        var config = new ExperimentConfig
        {
            Id          = "str-flag",
            SubjectType = "user",
            Variants    = [new VariantConfig { Name = "treatment", Allocation = 1.0, Value = "blue" }]
        };
        var provider = new KomentoFeatureProvider(BuildClient(config));
        var ctx = CtxFor("user-1");

        var result = await provider.ResolveStringValueAsync("str-flag", "default", ctx);

        result.Value.Should().Be("blue");
        result.Reason.Should().Be(Reason.TargetingMatch);
    }

    [Test]
    public async Task ResolveStringValue_parse_error_when_value_is_not_string()
    {
        var config = new ExperimentConfig
        {
            Id          = "int-as-str",
            SubjectType = "user",
            Variants    = [new VariantConfig { Name = "treatment", Allocation = 1.0, Value = 42 }]
        };
        var provider = new KomentoFeatureProvider(BuildClient(config));
        var ctx = CtxFor("user-1");

        var result = await provider.ResolveStringValueAsync("int-as-str", "default", ctx);

        result.Value.Should().Be("default");
        result.ErrorType.Should().Be(ErrorType.ParseError);
    }

    // ── int ───────────────────────────────────────────────────────────────────

    [Test]
    public async Task ResolveIntegerValue_returns_variant_value_on_match()
    {
        var config = new ExperimentConfig
        {
            Id          = "int-flag",
            SubjectType = "user",
            Variants    = [new VariantConfig { Name = "treatment", Allocation = 1.0, Value = 7 }]
        };
        var provider = new KomentoFeatureProvider(BuildClient(config));
        var ctx = CtxFor("user-1");

        var result = await provider.ResolveIntegerValueAsync("int-flag", 0, ctx);

        result.Value.Should().Be(7);
        result.Reason.Should().Be(Reason.TargetingMatch);
    }

    [Test]
    public async Task ResolveIntegerValue_parse_error_when_value_is_not_int()
    {
        var config = new ExperimentConfig
        {
            Id          = "bool-as-int",
            SubjectType = "user",
            Variants    = [new VariantConfig { Name = "treatment", Allocation = 1.0, Value = true }]
        };
        var provider = new KomentoFeatureProvider(BuildClient(config));
        var ctx = CtxFor("user-1");

        var result = await provider.ResolveIntegerValueAsync("bool-as-int", 0, ctx);

        result.Value.Should().Be(0);
        result.ErrorType.Should().Be(ErrorType.ParseError);
    }

    // ── double ────────────────────────────────────────────────────────────────

    [Test]
    public async Task ResolveDoubleValue_returns_variant_value_on_match()
    {
        var config = new ExperimentConfig
        {
            Id          = "dbl-flag",
            SubjectType = "user",
            Variants    = [new VariantConfig { Name = "treatment", Allocation = 1.0, Value = 3.14 }]
        };
        var provider = new KomentoFeatureProvider(BuildClient(config));
        var ctx = CtxFor("user-1");

        var result = await provider.ResolveDoubleValueAsync("dbl-flag", 0.0, ctx);

        result.Value.Should().Be(3.14);
        result.Reason.Should().Be(Reason.TargetingMatch);
    }

    [Test]
    public async Task ResolveDoubleValue_parse_error_when_value_is_not_double()
    {
        var config = new ExperimentConfig
        {
            Id          = "str-as-dbl",
            SubjectType = "user",
            Variants    = [new VariantConfig { Name = "treatment", Allocation = 1.0, Value = "pi" }]
        };
        var provider = new KomentoFeatureProvider(BuildClient(config));
        var ctx = CtxFor("user-1");

        var result = await provider.ResolveDoubleValueAsync("str-as-dbl", 0.0, ctx);

        result.Value.Should().Be(0.0);
        result.ErrorType.Should().Be(ErrorType.ParseError);
    }

    // ── structure: primitives ─────────────────────────────────────────────────

    [Test]
    public async Task ResolveStructureValue_bool_wraps_in_Value()
    {
        var config = new ExperimentConfig
        {
            Id          = "struct-bool",
            SubjectType = "user",
            Variants    = [new VariantConfig { Name = "treatment", Allocation = 1.0, Value = true }]
        };
        var provider = new KomentoFeatureProvider(BuildClient(config));
        var ctx = CtxFor("user-1");

        var result = await provider.ResolveStructureValueAsync("struct-bool", new Value(), ctx);

        result.Value.IsBoolean.Should().BeTrue();
        result.Value.AsBoolean.Should().BeTrue();
        result.Reason.Should().Be(Reason.TargetingMatch);
    }

    [Test]
    public async Task ResolveStructureValue_string_wraps_in_Value()
    {
        var config = new ExperimentConfig
        {
            Id          = "struct-str",
            SubjectType = "user",
            Variants    = [new VariantConfig { Name = "treatment", Allocation = 1.0, Value = "hello" }]
        };
        var provider = new KomentoFeatureProvider(BuildClient(config));
        var ctx = CtxFor("user-1");

        var result = await provider.ResolveStructureValueAsync("struct-str", new Value(), ctx);

        result.Value.IsString.Should().BeTrue();
        result.Value.AsString.Should().Be("hello");
    }

    [Test]
    public async Task ResolveStructureValue_int_wraps_in_Value()
    {
        var config = new ExperimentConfig
        {
            Id          = "struct-int",
            SubjectType = "user",
            Variants    = [new VariantConfig { Name = "treatment", Allocation = 1.0, Value = 99 }]
        };
        var provider = new KomentoFeatureProvider(BuildClient(config));
        var ctx = CtxFor("user-1");

        var result = await provider.ResolveStructureValueAsync("struct-int", new Value(), ctx);

        result.Value.IsNumber.Should().BeTrue();
        result.Value.AsInteger.Should().Be(99);
    }

    // ── structure: complex object via JSON ────────────────────────────────────

    [Test]
    public async Task ResolveStructureValue_complex_object_serializes_via_json()
    {
        var payload = new { Color = "red", Count = 3 };
        var config = new ExperimentConfig
        {
            Id          = "struct-complex",
            SubjectType = "user",
            Variants    = [new VariantConfig { Name = "treatment", Allocation = 1.0, Value = payload }]
        };
        var provider = new KomentoFeatureProvider(BuildClient(config));
        var ctx = CtxFor("user-1");

        var result = await provider.ResolveStructureValueAsync("struct-complex", new Value(), ctx);

        result.Value.IsStructure.Should().BeTrue();
        var structure = result.Value.AsStructure!;
        structure.GetValue("Color").AsString.Should().Be("red");
        structure.GetValue("Count").AsDouble.Should().Be(3);
        result.Reason.Should().Be(Reason.TargetingMatch);
    }

    // ── structure: parse error for non-serializable ───────────────────────────

    [Test]
    public async Task ResolveStructureValue_non_serializable_returns_ParseError()
    {
        var config = new ExperimentConfig
        {
            Id          = "struct-bad",
            SubjectType = "user",
            Variants    = [new VariantConfig { Name = "treatment", Allocation = 1.0, Value = new Action(() => { }) }]
        };
        var provider = new KomentoFeatureProvider(BuildClient(config));
        var ctx = CtxFor("user-1");

        var result = await provider.ResolveStructureValueAsync("struct-bad", new Value(), ctx);

        result.ErrorType.Should().Be(ErrorType.ParseError);
        result.Reason.Should().Be(Reason.Error);
    }

    [Test]
    public async Task ResolveStructureValue_null_variant_returns_ParseError()
    {
        var config = new ExperimentConfig
        {
            Id          = "struct-null",
            SubjectType = "user",
            Variants    = [new VariantConfig { Name = "treatment", Allocation = 1.0, Value = null }]
        };
        var provider = new KomentoFeatureProvider(BuildClient(config));
        var ctx = CtxFor("user-1");

        var result = await provider.ResolveStructureValueAsync("struct-null", new Value(), ctx);

        result.ErrorType.Should().Be(ErrorType.ParseError);
        result.Reason.Should().Be(Reason.Error);
    }

    [Test]
    public async Task ResolveStructureValue_array_serializes_via_json()
    {
        object[] payload = ["hello", false, 2];
        var config = new ExperimentConfig
        {
            Id          = "struct-array",
            SubjectType = "user",
            Variants    = [new VariantConfig { Name = "treatment", Allocation = 1.0, Value = payload }]
        };
        var provider = new KomentoFeatureProvider(BuildClient(config));
        var ctx = CtxFor("user-1");

        var result = await provider.ResolveStructureValueAsync("struct-array", new Value(), ctx);

        result.Value.IsList.Should().BeTrue();
        result.Value.AsList.Should().HaveCount(3);
        result.Value.AsList[0].AsString.Should().Be("hello");
        result.Value.AsList[1].AsBoolean.Should().BeFalse();
        result.Value.AsList[2].AsDouble.Should().Be(2);
    }

    // ── context attribute forwarding ──────────────────────────────────────────

    [Test]
    public async Task Context_attributes_forwarded_to_Komento_EvaluationContext()
    {
        var config = new ExperimentConfig
        {
            Id            = "admin-flag",
            SubjectType   = "user",
            Variants      = [new VariantConfig { Name = "treatment", Allocation = 1.0, Value = true }],
            GlobalFilters = [new TraitEqualsFilter { Key = "role", Value = "admin" }]
        };
        var provider = new KomentoFeatureProvider(BuildClient(config));

        var ctx = OFContext.Builder()
            .SetTargetingKey("user-1")
            .Set("role", new Value("admin"))
            .Build();

        var result = await provider.ResolveBooleanValueAsync("admin-flag", false, ctx);

        result.Value.Should().BeTrue();
        result.Reason.Should().Be(Reason.TargetingMatch);
    }

    [Test]
    public async Task Context_attribute_role_missing_causes_ineligible()
    {
        var config = new ExperimentConfig
        {
            Id            = "admin-flag2",
            SubjectType   = "user",
            Variants      = [new VariantConfig { Name = "treatment", Allocation = 1.0, Value = true }],
            GlobalFilters = [new TraitEqualsFilter { Key = "role", Value = "admin" }]
        };
        var provider = new KomentoFeatureProvider(BuildClient(config));

        var ctx = OFContext.Builder()
            .SetTargetingKey("user-1")
            .Build();

        var result = await provider.ResolveBooleanValueAsync("admin-flag2", false, ctx);

        result.Value.Should().BeFalse();
        result.Reason.Should().Be(Reason.Default);
    }

    [Test]
    public async Task SubjectType_attribute_is_forwarded_as_context_attribute()
    {
        var config = new ExperimentConfig
        {
            Id            = "sub-type-flag",
            SubjectType   = "user",
            Variants      = [new VariantConfig { Name = "treatment", Allocation = 1.0, Value = true }],
            GlobalFilters = [new TraitEqualsFilter { Key = "subjectType", Value = "device" }]
        };
        var provider = new KomentoFeatureProvider(BuildClient(config));

        var ctx = OFContext.Builder()
            .SetTargetingKey("device-123")
            .Set("subjectType", new Value("device"))
            .Build();

        var result = await provider.ResolveBooleanValueAsync("sub-type-flag", false, ctx);

        result.Value.Should().BeTrue();
        result.Reason.Should().Be(Reason.TargetingMatch);
    }

    [Test]
    public async Task Boolean_and_number_context_attributes_are_forwarded()
    {
        var client = new CapturingExperimentClient(new VariantResult
        {
            VariantName = "treatment",
            Value = true,
            IsEligible = true
        });
        var provider = new KomentoFeatureProvider(client);
        var ignored = Structure.Builder().Set("x", "y").Build();
        var ctx = OFContext.Builder()
            .SetTargetingKey("user-1")
            .Set("isAdmin", new Value(true))
            .Set("quota", new Value(42))
            .Set("ignored", new Value(ignored))
            .Build();

        var result = await provider.ResolveBooleanValueAsync("bool-flag", false, ctx);

        result.Value.Should().BeTrue();
        client.LastSubjectId.Should().Be("user-1");
        client.LastContext.TryGetValue("isAdmin", out var isAdmin).Should().BeTrue();
        isAdmin.Should().Be(true);
        client.LastContext.TryGetValue("quota", out var quota).Should().BeTrue();
        quota.Should().Be(42d);
        client.LastContext.TryGetValue("ignored", out _).Should().BeFalse();
    }

    private sealed class CapturingExperimentClient(VariantResult result) : IExperimentClient
    {
        public string? LastSubjectId { get; private set; }
        public EvaluationContext LastContext { get; private set; } = EvaluationContext.Empty;

        public bool ExperimentExists(string flagKey) => true;

        public ValueTask<VariantResult> GetVariantAsync(string flagKey, string subjectId, in EvaluationContext ctx, CancellationToken ct = default)
        {
            LastSubjectId = subjectId;
            LastContext = ctx;
            return ValueTask.FromResult(result);
        }

        public ValueTask<bool> GetBoolAsync(string flagKey, string subjectId, in EvaluationContext ctx, bool defaultValue = default, CancellationToken ct = default)
            => throw new NotSupportedException();

        public ValueTask<string> GetStringAsync(string flagKey, string subjectId, in EvaluationContext ctx, string defaultValue = "", CancellationToken ct = default)
            => throw new NotSupportedException();

        public ValueTask<int> GetIntAsync(string flagKey, string subjectId, in EvaluationContext ctx, int defaultValue = default, CancellationToken ct = default)
            => throw new NotSupportedException();

        public ValueTask<double> GetDoubleAsync(string flagKey, string subjectId, in EvaluationContext ctx, double defaultValue = default, CancellationToken ct = default)
            => throw new NotSupportedException();
    }
}

using AwesomeAssertions;
using Komento.Internals;
using TUnit.Core;

namespace Komento.Tests;

public class ExperimentConfigComparerTests
{
    private static ExperimentConfig Base(
        string id = "exp",
        string subjectType = "user",
        double control = 0.5,
        object? value = null,
        IReadOnlyList<FilterConfig>? filters = null,
        IReadOnlyList<OverrideRule>? overrides = null) => new()
    {
        Id            = id,
        SubjectType   = subjectType,
        Variants      =
        [
            new VariantConfig { Name = "control",   Allocation = control },
            new VariantConfig { Name = "treatment", Allocation = 1.0 - control, Value = value }
        ],
        GlobalFilters = filters   ?? [new TraitEqualsFilter { Key = "region", Value = "eu" }, new SegmentIncludeFilter { Segment = "beta" }],
        Overrides     = overrides ?? [new SubjectOverride { SubjectId = "vip", Variant = "treatment" }, new SegmentOverride { Segment = "staff", Variant = "treatment" }]
    };

    [Test]
    public void Two_separately_built_configs_with_the_same_content_are_equal()
        => ExperimentConfigComparer.AreEqual(Base(value: true), Base(value: true)).Should().BeTrue();

    [Test]
    public void A_different_subject_type_is_a_change()
        => ExperimentConfigComparer.AreEqual(Base(), Base(subjectType: "device")).Should().BeFalse();

    [Test]
    public void A_different_allocation_is_a_change()
        => ExperimentConfigComparer.AreEqual(Base(control: 0.5), Base(control: 0.9)).Should().BeFalse();

    [Test]
    public void A_different_variant_value_is_a_change()
        => ExperimentConfigComparer.AreEqual(Base(value: "a"), Base(value: "b")).Should().BeFalse();

    [Test]
    public void A_different_filter_is_a_change()
        => ExperimentConfigComparer.AreEqual(
            Base(filters: [new TraitEqualsFilter { Key = "region", Value = "eu" }]),
            Base(filters: [new TraitEqualsFilter { Key = "region", Value = "us" }])).Should().BeFalse();

    [Test]
    public void A_different_override_is_a_change()
        => ExperimentConfigComparer.AreEqual(
            Base(overrides: [new SubjectOverride { SubjectId = "a", Variant = "treatment" }]),
            Base(overrides: [new SubjectOverride { SubjectId = "b", Variant = "treatment" }])).Should().BeFalse();

    [Test]
    public void A_filter_or_override_of_another_kind_at_the_same_position_is_a_change()
    {
        ExperimentConfigComparer.AreEqual(
            Base(filters: [new TraitEqualsFilter { Key = "k", Value = "v" }]),
            Base(filters: [new SegmentIncludeFilter { Segment = "k" }])).Should().BeFalse();
        ExperimentConfigComparer.AreEqual(
            Base(overrides: [new SubjectOverride { SubjectId = "s", Variant = "treatment" }]),
            Base(overrides: [new SegmentOverride { Segment = "s", Variant = "treatment" }])).Should().BeFalse();
    }

    [Test]
    public void A_value_that_cannot_be_compared_reliably_counts_as_a_change()
    {
        // Two distinct instances of a type without value equality: treated as changed, never as unchanged.
        ExperimentConfigComparer.AreEqual(Base(value: new object()), Base(value: new object())).Should().BeFalse();
    }
}

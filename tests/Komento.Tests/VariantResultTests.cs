using AwesomeAssertions;
using Komento;
using TUnit.Core;

namespace Komento.Tests;

public class VariantResultTests
{
    [Test]
    public void Equality_operator_matches_variant_name()
    {
        var result = new VariantResult { VariantName = "treatment", IsEligible = true };
        (result == "treatment").Should().BeTrue();
        (result == "control").Should().BeFalse();
        (result != "control").Should().BeTrue();
    }

    [Test]
    public void NotFound_has_control_name_and_is_not_eligible()
    {
        var r = VariantResult.NotFound;
        r.VariantName.Should().Be("control");
        r.IsEligible.Should().BeFalse();
        r.IsOutsider.Should().BeFalse();
    }

    [Test]
    public void Ineligible_has_control_name_and_is_not_eligible()
    {
        var r = VariantResult.Ineligible;
        r.VariantName.Should().Be("control");
        r.IsEligible.Should().BeFalse();
        r.IsOutsider.Should().BeFalse();
    }

    [Test]
    public void Outsider_has_control_name_is_eligible_and_is_outsider()
    {
        var r = VariantResult.Outsider();
        r.VariantName.Should().Be("control");
        r.IsEligible.Should().BeTrue();
        r.IsOutsider.Should().BeTrue();
    }

    [Test]
    public void NotFound_and_Ineligible_are_value_equal_by_design()
    {
        // By design: both result in control behavior; callers do not need to distinguish them.
        VariantResult.NotFound.Should().Be(VariantResult.Ineligible);
    }

    [Test]
    public void Equals_returns_true_for_structurally_identical_results()
    {
        var a = new VariantResult { VariantName = "treatment", IsEligible = true, IsOutsider = false };
        var b = new VariantResult { VariantName = "treatment", IsEligible = true, IsOutsider = false };
        a.Equals(b).Should().BeTrue();
    }

    [Test]
    public void Equals_returns_false_for_different_variant_name()
    {
        var a = new VariantResult { VariantName = "treatment", IsEligible = true };
        var b = new VariantResult { VariantName = "control",   IsEligible = true };
        a.Equals(b).Should().BeFalse();
    }

    [Test]
    public void Equals_object_returns_false_for_non_VariantResult_type()
    {
        var r = new VariantResult { VariantName = "treatment", IsEligible = true };
        r.Equals("treatment").Should().BeFalse();
        r.Equals(null).Should().BeFalse();
    }

    [Test]
    public void GetHashCode_is_equal_for_structurally_identical_results()
    {
        var a = new VariantResult { VariantName = "treatment", IsEligible = true, IsOutsider = false };
        var b = new VariantResult { VariantName = "treatment", IsEligible = true, IsOutsider = false };
        a.GetHashCode().Should().Be(b.GetHashCode());
    }

    [Test]
    public void GetHashCode_differs_for_different_results()
    {
        var a = new VariantResult { VariantName = "treatment", IsEligible = true };
        var b = new VariantResult { VariantName = "control",   IsEligible = true };
        a.GetHashCode().Should().NotBe(b.GetHashCode());
    }
}

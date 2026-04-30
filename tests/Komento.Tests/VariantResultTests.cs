using AwesomeAssertions;
using Komento;
using Xunit;

namespace Komento.Tests;

public class VariantResultTests
{
    [Fact]
    public void Equality_operator_matches_variant_name()
    {
        var result = new VariantResult { VariantName = "treatment", IsEligible = true };
        (result == "treatment").Should().BeTrue();
        (result == "control").Should().BeFalse();
        (result != "control").Should().BeTrue();
    }

    [Fact]
    public void NotFound_has_control_name_and_is_not_eligible()
    {
        var r = VariantResult.NotFound;
        r.VariantName.Should().Be("control");
        r.IsEligible.Should().BeFalse();
        r.IsOutsider.Should().BeFalse();
    }

    [Fact]
    public void Ineligible_has_control_name_and_is_not_eligible()
    {
        var r = VariantResult.Ineligible;
        r.VariantName.Should().Be("control");
        r.IsEligible.Should().BeFalse();
        r.IsOutsider.Should().BeFalse();
    }

    [Fact]
    public void Outsider_has_control_name_is_eligible_and_is_outsider()
    {
        var r = VariantResult.Outsider();
        r.VariantName.Should().Be("control");
        r.IsEligible.Should().BeTrue();
        r.IsOutsider.Should().BeTrue();
    }

    [Fact]
    public void NotFound_and_Ineligible_are_value_equal_by_design()
    {
        // By design: both result in control behavior; callers do not need to distinguish them.
        VariantResult.NotFound.Should().Be(VariantResult.Ineligible);
    }
}

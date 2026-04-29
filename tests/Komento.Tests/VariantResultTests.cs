using Komento;
using Xunit;

namespace Komento.Tests;

public class VariantResultTests
{
    [Fact]
    public void Equality_operator_matches_variant_name()
    {
        var result = new VariantResult { VariantName = "treatment", IsEligible = true };
        Assert.True(result == "treatment");
        Assert.False(result == "control");
        Assert.True(result != "control");
    }

    [Fact]
    public void NotFound_has_control_name_and_is_not_eligible()
    {
        var r = VariantResult.NotFound;
        Assert.Equal("control", r.VariantName);
        Assert.False(r.IsEligible);
        Assert.False(r.IsOutsider);
    }

    [Fact]
    public void Ineligible_has_control_name_and_is_not_eligible()
    {
        var r = VariantResult.Ineligible;
        Assert.Equal("control", r.VariantName);
        Assert.False(r.IsEligible);
        Assert.False(r.IsOutsider);
    }

    [Fact]
    public void Outsider_has_control_name_is_eligible_and_is_outsider()
    {
        var r = VariantResult.Outsider();
        Assert.Equal("control", r.VariantName);
        Assert.True(r.IsEligible);
        Assert.True(r.IsOutsider);
    }

    [Fact]
    public void NotFound_and_Ineligible_are_value_equal_by_design()
    {
        // By design: both result in control behavior; callers do not need to distinguish them.
        Assert.Equal(VariantResult.NotFound, VariantResult.Ineligible);
    }
}

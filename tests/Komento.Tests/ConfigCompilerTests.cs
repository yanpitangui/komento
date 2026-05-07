using AwesomeAssertions;
using Komento;
using Komento.Internals;
using TUnit.Core;

namespace Komento.Tests;

public class ConfigCompilerTests
{
    private static ExperimentConfig MakeConfig(params (string name, double allocation, object? value)[] variants)
        => new()
        {
            Id          = "test-exp",
            SubjectType = "user",
            Variants    = variants.Select(v => new VariantConfig { Name = v.name, Allocation = v.allocation, Value = v.value }).ToList()
        };

    [Test]
    public void Fifty_fifty_split_produces_correct_ranges()
    {
        var config   = MakeConfig(("control", 0.5, null), ("treatment", 0.5, true));
        var compiled = ConfigCompiler.Compile(config);

        compiled.Variants.Length.Should().Be(2);
        compiled.Variants[0].Name.Should().Be("control");
        compiled.Variants[0].Ranges[0].Start.Should().Be(1);
        compiled.Variants[0].Ranges[0].End.Should().Be(500);
        compiled.Variants[1].Name.Should().Be("treatment");
        compiled.Variants[1].Ranges[0].Start.Should().Be(501);
        compiled.Variants[1].Ranges[0].End.Should().Be(1000);
        compiled.Variants[1].Value.Should().Be(true);
    }

    [Test]
    public void Three_way_equal_split_fills_all_buckets()
    {
        var config   = MakeConfig(("a", 0.34, null), ("b", 0.33, null), ("c", 0.33, null));
        var compiled = ConfigCompiler.Compile(config);

        compiled.Variants.Length.Should().Be(3);
        compiled.Variants[0].Ranges[0].Start.Should().Be(1);
        compiled.Variants[^1].Ranges[^1].End.Should().Be(1000);
    }

    [Test]
    public void Single_variant_full_allocation_covers_all_buckets()
    {
        var config   = MakeConfig(("control", 1.0, null));
        var compiled = ConfigCompiler.Compile(config);

        compiled.Variants.Should().ContainSingle();
        compiled.Variants[0].Ranges[0].Start.Should().Be(1);
        compiled.Variants[0].Ranges[0].End.Should().Be(1000);
    }

    [Test]
    public void Partial_allocation_leaves_uncovered_buckets()
    {
        var config   = MakeConfig(("control", 0.5, null));
        var compiled = ConfigCompiler.Compile(config);

        compiled.Variants.Should().ContainSingle();
        compiled.Variants[0].Ranges[0].Start.Should().Be(1);
        compiled.Variants[0].Ranges[0].End.Should().Be(500);
    }

    [Test]
    public void Compiled_carries_filters_and_overrides_as_arrays()
    {
        var config = new ExperimentConfig
        {
            Id            = "exp",
            SubjectType   = "user",
            Variants      = [new VariantConfig { Name = "control", Allocation = 1.0 }],
            GlobalFilters = [new TraitEqualsFilter { Key = "country", Value = "BR" }],
            Overrides     = [new SubjectOverride  { SubjectId = "u1", Variant = "control" }]
        };
        var compiled = ConfigCompiler.Compile(config);

        compiled.Filters.Should().ContainSingle();
        compiled.Overrides.Should().ContainSingle();
        compiled.Id.Should().Be("exp");
        compiled.SubjectType.Should().Be("user");
    }
}

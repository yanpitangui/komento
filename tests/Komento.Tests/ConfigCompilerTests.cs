using Komento;
using Komento.Internals;
using Xunit;

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

    [Fact]
    public void Fifty_fifty_split_produces_correct_ranges()
    {
        var config   = MakeConfig(("control", 0.5, null), ("treatment", 0.5, true));
        var compiled = ConfigCompiler.Compile(config);

        Assert.Equal(2, compiled.Variants.Length);
        Assert.Equal("control",   compiled.Variants[0].Name);
        Assert.Equal(1,           compiled.Variants[0].Ranges[0].Start);
        Assert.Equal(500,         compiled.Variants[0].Ranges[0].End);
        Assert.Equal("treatment", compiled.Variants[1].Name);
        Assert.Equal(501,         compiled.Variants[1].Ranges[0].Start);
        Assert.Equal(1000,        compiled.Variants[1].Ranges[0].End);
        Assert.Equal(true,        compiled.Variants[1].Value);
    }

    [Fact]
    public void Three_way_equal_split_fills_all_buckets()
    {
        var config   = MakeConfig(("a", 0.34, null), ("b", 0.33, null), ("c", 0.33, null));
        var compiled = ConfigCompiler.Compile(config);

        Assert.Equal(3, compiled.Variants.Length);
        Assert.Equal(1, compiled.Variants[0].Ranges[0].Start);
        Assert.Equal(1000, compiled.Variants[^1].Ranges[^1].End);
    }

    [Fact]
    public void Single_variant_full_allocation_covers_all_buckets()
    {
        var config   = MakeConfig(("control", 1.0, null));
        var compiled = ConfigCompiler.Compile(config);

        Assert.Single(compiled.Variants);
        Assert.Equal(1,    compiled.Variants[0].Ranges[0].Start);
        Assert.Equal(1000, compiled.Variants[0].Ranges[0].End);
    }

    [Fact]
    public void Partial_allocation_leaves_uncovered_buckets()
    {
        var config   = MakeConfig(("control", 0.5, null));
        var compiled = ConfigCompiler.Compile(config);

        Assert.Single(compiled.Variants);
        Assert.Equal(1,   compiled.Variants[0].Ranges[0].Start);
        Assert.Equal(500, compiled.Variants[0].Ranges[0].End);
    }

    [Fact]
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

        Assert.Single(compiled.Filters);
        Assert.Single(compiled.Overrides);
        Assert.Equal("exp",  compiled.Id);
        Assert.Equal("user", compiled.SubjectType);
    }
}

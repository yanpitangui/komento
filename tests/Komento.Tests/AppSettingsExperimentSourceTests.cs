using System.Text;
using Komento;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Komento.Tests;

public class AppSettingsExperimentSourceTests
{
    private static IConfiguration BuildConfig(string json) =>
        new ConfigurationBuilder()
            .AddJsonStream(new MemoryStream(Encoding.UTF8.GetBytes(json)))
            .Build();

    private const string SampleJson = """
    {
      "Komento": {
        "Experiments": [
          {
            "Id": "checkout-flow",
            "SubjectType": "user",
            "Variants": [
              { "Name": "control",   "Allocation": 0.5 },
              { "Name": "treatment", "Allocation": 0.5, "Value": "true" }
            ],
            "GlobalFilters": [
              { "Type": "trait-equals", "Key": "country", "Value": "BR" }
            ],
            "Overrides": [
              { "Type": "subject", "SubjectId": "user-42",        "Variant": "treatment" },
              { "Type": "segment", "Segment":   "internal-staff", "Variant": "treatment" }
            ]
          },
          {
            "Id": "dark-mode",
            "SubjectType": "user",
            "Variants": [
              { "Name": "control",   "Allocation": 0.5 },
              { "Name": "treatment", "Allocation": 0.5 }
            ]
          }
        ]
      }
    }
    """;

    [Fact]
    public async Task Loads_only_requested_experiment_ids()
    {
        var source = new AppSettingsExperimentSource(BuildConfig(SampleJson));
        var result = await source.LoadAsync(new HashSet<string> { "dark-mode" });

        Assert.Single(result);
        Assert.True(result.ContainsKey("dark-mode"));
        Assert.False(result.ContainsKey("checkout-flow"));
    }

    [Fact]
    public async Task Deserializes_variants_with_allocation_and_coerced_value()
    {
        var source = new AppSettingsExperimentSource(BuildConfig(SampleJson));
        var result = await source.LoadAsync(new HashSet<string> { "checkout-flow" });
        var config = result["checkout-flow"];

        Assert.Equal(2,           config.Variants.Count);
        Assert.Equal("treatment", config.Variants[1].Name);
        Assert.Equal(0.5,         config.Variants[1].Allocation);
        Assert.Equal(true,        config.Variants[1].Value);
    }

    [Fact]
    public async Task Deserializes_trait_equals_filter()
    {
        var source = new AppSettingsExperimentSource(BuildConfig(SampleJson));
        var result = await source.LoadAsync(new HashSet<string> { "checkout-flow" });
        var config = result["checkout-flow"];

        Assert.Single(config.GlobalFilters);
        var filter = Assert.IsType<TraitEqualsFilter>(config.GlobalFilters[0]);
        Assert.Equal("country", filter.Key);
        Assert.Equal("BR",      filter.Value);
    }

    [Fact]
    public async Task Deserializes_subject_and_segment_overrides()
    {
        var source = new AppSettingsExperimentSource(BuildConfig(SampleJson));
        var result = await source.LoadAsync(new HashSet<string> { "checkout-flow" });
        var config = result["checkout-flow"];

        Assert.Equal(2, config.Overrides.Count);
        Assert.IsType<SubjectOverride>(config.Overrides[0]);
        Assert.IsType<SegmentOverride>(config.Overrides[1]);
    }

    [Fact]
    public async Task Returns_empty_when_no_ids_match()
    {
        var source = new AppSettingsExperimentSource(BuildConfig(SampleJson));
        var result = await source.LoadAsync(new HashSet<string> { "non-existent" });
        Assert.Empty(result);
    }

    [Fact]
    public async Task Custom_section_path_is_respected()
    {
        var json = """
        {
          "MyApp": {
            "Experiments": [
              {
                "Id": "feature-x",
                "SubjectType": "user",
                "Variants": [ { "Name": "control", "Allocation": 1.0 } ]
              }
            ]
          }
        }
        """;
        var source = new AppSettingsExperimentSource(BuildConfig(json), sectionPath: "MyApp");
        var result = await source.LoadAsync(new HashSet<string> { "feature-x" });
        Assert.Single(result);
    }
}

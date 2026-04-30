using System.Text;
using AwesomeAssertions;
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

        result.Should().ContainSingle();
        result.ContainsKey("dark-mode").Should().BeTrue();
        result.ContainsKey("checkout-flow").Should().BeFalse();
    }

    [Fact]
    public async Task Deserializes_variants_with_allocation_and_coerced_value()
    {
        var source = new AppSettingsExperimentSource(BuildConfig(SampleJson));
        var result = await source.LoadAsync(new HashSet<string> { "checkout-flow" });
        var config = result["checkout-flow"];

        config.Variants.Count.Should().Be(2);
        config.Variants[1].Name.Should().Be("treatment");
        config.Variants[1].Allocation.Should().Be(0.5);
        config.Variants[1].Value.Should().Be(true);
    }

    [Fact]
    public async Task Deserializes_trait_equals_filter()
    {
        var source = new AppSettingsExperimentSource(BuildConfig(SampleJson));
        var result = await source.LoadAsync(new HashSet<string> { "checkout-flow" });
        var config = result["checkout-flow"];

        config.GlobalFilters.Should().ContainSingle();
        var filter = config.GlobalFilters[0].Should().BeOfType<TraitEqualsFilter>().Subject;
        filter.Key.Should().Be("country");
        filter.Value.Should().Be("BR");
    }

    [Fact]
    public async Task Deserializes_subject_and_segment_overrides()
    {
        var source = new AppSettingsExperimentSource(BuildConfig(SampleJson));
        var result = await source.LoadAsync(new HashSet<string> { "checkout-flow" });
        var config = result["checkout-flow"];

        config.Overrides.Count.Should().Be(2);
        config.Overrides[0].Should().BeOfType<SubjectOverride>();
        config.Overrides[1].Should().BeOfType<SegmentOverride>();
    }

    [Fact]
    public async Task Returns_empty_when_no_ids_match()
    {
        var source = new AppSettingsExperimentSource(BuildConfig(SampleJson));
        var result = await source.LoadAsync(new HashSet<string> { "non-existent" });
        result.Should().BeEmpty();
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
        result.Should().ContainSingle();
    }
}

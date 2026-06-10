using System.Text;
using AwesomeAssertions;
using Komento;
using Microsoft.Extensions.Configuration;
using TUnit.Core;

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

    [Test]
    public async Task Loads_only_requested_experiment_ids()
    {
        var source = new AppSettingsExperimentSource(BuildConfig(SampleJson));
        var result = await source.LoadAsync(new HashSet<string> { "dark-mode" });

        result.Should().ContainSingle();
        result.ContainsKey("dark-mode").Should().BeTrue();
        result.ContainsKey("checkout-flow").Should().BeFalse();
    }

    [Test]
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

    [Test]
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

    [Test]
    public async Task Deserializes_subject_and_segment_overrides()
    {
        var source = new AppSettingsExperimentSource(BuildConfig(SampleJson));
        var result = await source.LoadAsync(new HashSet<string> { "checkout-flow" });
        var config = result["checkout-flow"];

        config.Overrides.Count.Should().Be(2);
        config.Overrides[0].Should().BeOfType<SubjectOverride>();
        config.Overrides[1].Should().BeOfType<SegmentOverride>();
    }

    [Test]
    public async Task Returns_empty_when_no_ids_match()
    {
        var source = new AppSettingsExperimentSource(BuildConfig(SampleJson));
        var result = await source.LoadAsync(new HashSet<string> { "non-existent" });
        result.Should().BeEmpty();
    }

    [Test]
    public async Task Empty_set_loads_all_experiments()
    {
        var source = new AppSettingsExperimentSource(BuildConfig(SampleJson));
        var result = await source.LoadAsync(new HashSet<string>());

        result.Count.Should().Be(2);
        result.ContainsKey("checkout-flow").Should().BeTrue();
        result.ContainsKey("dark-mode").Should().BeTrue();
    }

    [Test]
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

    // ── Value coercion ────────────────────────────────────────────────────────

    [Test]
    public async Task CoerceValue_parses_integer_string()
    {
        var json = """
        {
          "Komento": {
            "Experiments": [{
              "Id": "int-exp",
              "SubjectType": "user",
              "Variants": [ { "Name": "v1", "Allocation": 1.0, "Value": "42" } ]
            }]
          }
        }
        """;
        var source = new AppSettingsExperimentSource(BuildConfig(json));
        var result = await source.LoadAsync(new HashSet<string> { "int-exp" });
        result["int-exp"].Variants[0].Value.Should().Be(42);
    }

    [Test]
    public async Task CoerceValue_parses_double_string()
    {
        var json = """
        {
          "Komento": {
            "Experiments": [{
              "Id": "dbl-exp",
              "SubjectType": "user",
              "Variants": [ { "Name": "v1", "Allocation": 1.0, "Value": "3.14" } ]
            }]
          }
        }
        """;
        var source = new AppSettingsExperimentSource(BuildConfig(json));
        var result = await source.LoadAsync(new HashSet<string> { "dbl-exp" });
        result["dbl-exp"].Variants[0].Value.Should().Be(3.14);
    }

    [Test]
    public async Task CoerceValue_keeps_unrecognised_string_as_string()
    {
        var json = """
        {
          "Komento": {
            "Experiments": [{
              "Id": "str-exp",
              "SubjectType": "user",
              "Variants": [ { "Name": "v1", "Allocation": 1.0, "Value": "red" } ]
            }]
          }
        }
        """;
        var source = new AppSettingsExperimentSource(BuildConfig(json));
        var result = await source.LoadAsync(new HashSet<string> { "str-exp" });
        result["str-exp"].Variants[0].Value.Should().Be("red");
    }

    // ── Error paths ───────────────────────────────────────────────────────────

    [Test]
    public async Task Unknown_filter_type_throws_InvalidOperationException()
    {
        var json = """
        {
          "Komento": {
            "Experiments": [{
              "Id": "bad-filter",
              "SubjectType": "user",
              "Variants": [ { "Name": "v1", "Allocation": 1.0 } ],
              "GlobalFilters": [ { "Type": "unknown-filter" } ]
            }]
          }
        }
        """;
        var source = new AppSettingsExperimentSource(BuildConfig(json));
        var act = () => source.LoadAsync(new HashSet<string> { "bad-filter" }).AsTask();
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*unknown-filter*");
    }

    [Test]
    public async Task Unknown_override_type_throws_InvalidOperationException()
    {
        var json = """
        {
          "Komento": {
            "Experiments": [{
              "Id": "bad-override",
              "SubjectType": "user",
              "Variants": [ { "Name": "v1", "Allocation": 1.0 } ],
              "Overrides": [ { "Type": "unknown-override" } ]
            }]
          }
        }
        """;
        var source = new AppSettingsExperimentSource(BuildConfig(json));
        var act = () => source.LoadAsync(new HashSet<string> { "bad-override" }).AsTask();
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*unknown-override*");
    }
}

using System.Collections.Immutable;
using System.Text;
using AwesomeAssertions;
using Komento.Generators;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using TUnit.Core;

namespace Komento.Generators.Tests;

public class KomentoGeneratorTests
{
    private static string RunGenerator(string json)
    {
        var compilation = CSharpCompilation.Create("TestAssembly");
        GeneratorDriver driver = CSharpGeneratorDriver.Create(new KomentoGenerator());
        driver = driver.AddAdditionalTexts(
            ImmutableArray.Create<AdditionalText>(new InMemoryAdditionalText("komento.json", json)));

        driver.RunGeneratorsAndUpdateCompilation(compilation, out var newComp, out _);

        return newComp.SyntaxTrees
            .FirstOrDefault(t => t.FilePath.Contains("KomentoExperiments"))
            ?.GetText().ToString() ?? string.Empty;
    }

    [Test]
    public void Generates_class_with_AllIds_and_nested_constants()
    {
        var source = RunGenerator("""{ "Experiments": ["checkout-flow", "dark-mode"] }""");

        source.Should().Contain("KomentoExperiments");
        source.Should().Contain("AllIds");
        source.Should().Contain("\"checkout-flow\"");
        source.Should().Contain("\"dark-mode\"");
        source.Should().Contain("class CheckoutFlow");
        source.Should().Contain("class DarkMode");
    }

    [Test]
    public void Nested_class_Id_const_matches_raw_id()
    {
        var source = RunGenerator("""{ "Experiments": ["my-flag"] }""");

        source.Should().Contain("class MyFlag");
        source.Should().Contain("const string Id = \"my-flag\"");
    }

    [Test]
    public void Empty_experiments_array_generates_nothing()
    {
        RunGenerator("""{ "Experiments": [] }""").Should().BeEmpty();
    }

    [Test]
    public void Missing_experiments_key_generates_nothing()
    {
        RunGenerator("{}").Should().BeEmpty();
    }

    [Test]
    public void Underscore_separated_ids_are_pascal_cased()
    {
        var source = RunGenerator("""{ "Experiments": ["my_feature_flag"] }""");

        source.Should().Contain("class MyFeatureFlag");
        source.Should().Contain("const string Id = \"my_feature_flag\"");
    }
}

file sealed class InMemoryAdditionalText(string path, string text) : AdditionalText
{
    public override string Path => path;
    public override SourceText? GetText(CancellationToken cancellationToken = default)
        => SourceText.From(text, Encoding.UTF8);
}

using AwesomeAssertions;
using Komento;
using Xunit;

namespace Komento.Tests;

public class EvaluationContextTests
{
    [Fact]
    public void Empty_context_returns_false_for_any_key()
    {
        var ctx = EvaluationContext.Empty;
        ctx.TryGetValue("any", out _).Should().BeFalse();
    }

    [Fact]
    public void Builder_sets_and_retrieves_value()
    {
        var ctx = EvaluationContext.Create()
            .Set("country", "BR")
            .Set("platform", "android")
            .Build();

        ctx.TryGetValue("country", out var country).Should().BeTrue();
        country.Should().Be("BR");
        ctx.TryGetValue("platform", out var platform).Should().BeTrue();
        platform.Should().Be("android");
    }

    [Fact]
    public void Builder_later_set_wins_for_same_key()
    {
        var ctx = EvaluationContext.Create()
            .Set("key", "first")
            .Set("key", "second")
            .Build();

        ctx.TryGetValue("key", out var value).Should().BeTrue();
        value.Should().Be("second");
    }

    [Fact]
    public void CreateFrom_copies_existing_context_attributes()
    {
        var baseCtx = EvaluationContext.Create().Set("region", "EU").Build();
        var ctx = EvaluationContextBuilder.CreateFrom(in baseCtx).Set("locale", "en-GB").Build();

        ctx.TryGetValue("region", out var region).Should().BeTrue();
        region.Should().Be("EU");
        ctx.TryGetValue("locale", out var locale).Should().BeTrue();
        locale.Should().Be("en-GB");
    }

    [Fact]
    public void CreateFrom_empty_context_produces_empty_builder()
    {
        var ctx = EvaluationContextBuilder.CreateFrom(in EvaluationContext.Empty).Build();
        ctx.TryGetValue("any", out _).Should().BeFalse();
    }
}

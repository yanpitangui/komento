using Komento;
using Xunit;

namespace Komento.Tests;

public class EvaluationContextTests
{
    [Fact]
    public void Empty_context_returns_false_for_any_key()
    {
        var ctx = EvaluationContext.Empty;
        Assert.False(ctx.TryGetValue("any", out _));
    }

    [Fact]
    public void Builder_sets_and_retrieves_value()
    {
        var ctx = EvaluationContext.Create()
            .Set("country", "BR")
            .Set("platform", "android")
            .Build();

        Assert.True(ctx.TryGetValue("country", out var country));
        Assert.Equal("BR", country);
        Assert.True(ctx.TryGetValue("platform", out var platform));
        Assert.Equal("android", platform);
    }

    [Fact]
    public void Builder_later_set_wins_for_same_key()
    {
        var ctx = EvaluationContext.Create()
            .Set("key", "first")
            .Set("key", "second")
            .Build();

        Assert.True(ctx.TryGetValue("key", out var value));
        Assert.Equal("second", value);
    }

    [Fact]
    public void CreateFrom_copies_existing_context_attributes()
    {
        var baseCtx = EvaluationContext.Create().Set("region", "EU").Build();
        var ctx = EvaluationContextBuilder.CreateFrom(in baseCtx).Set("locale", "en-GB").Build();

        Assert.True(ctx.TryGetValue("region", out var region));
        Assert.Equal("EU", region);
        Assert.True(ctx.TryGetValue("locale", out var locale));
        Assert.Equal("en-GB", locale);
    }

    [Fact]
    public void CreateFrom_empty_context_produces_empty_builder()
    {
        var ctx = EvaluationContextBuilder.CreateFrom(in EvaluationContext.Empty).Build();
        Assert.False(ctx.TryGetValue("any", out _));
    }
}

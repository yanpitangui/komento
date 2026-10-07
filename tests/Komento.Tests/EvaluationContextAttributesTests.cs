using AwesomeAssertions;
using TUnit.Core;

namespace Komento.Tests;

public class EvaluationContextAttributesTests
{
    [Test]
    public void Attributes_lists_what_was_set()
    {
        var ctx = EvaluationContext.Create().Set("plan", "premium").Set("beta", true).Build();

        ctx.Attributes.Should().HaveCount(2);
        ctx.Attributes["plan"].Should().Be("premium");
        ctx.Attributes["beta"].Should().Be(true);
    }

    [Test]
    public void Attributes_of_an_empty_context_is_empty()
    {
        EvaluationContext.Empty.Attributes.Should().BeEmpty();
    }
}

using AwesomeAssertions;
using Komento.AspNetCore;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using TUnit.Core;

namespace Komento.AspNetCore.Tests;

public class KomentoRequestContextTests
{
    private sealed class HeaderSubjectProvider : ISubjectProvider
    {
        public string  SubjectType => "user";
        public string? GetSubject(HttpContext context) => context.Request.Headers["x-user"].FirstOrDefault();
    }

    private sealed class PlanEnricher : IEvaluationContextEnricher
    {
        public ValueTask EnrichAsync(HttpContext context, EvaluationContextBuilder builder, CancellationToken ct = default)
        {
            if (context.Request.Headers["x-plan"].FirstOrDefault() is { } plan) builder.Set("plan", plan);
            return ValueTask.CompletedTask;
        }
    }

    private static HttpContext RequestWith(params (string Name, string Value)[] headers)
    {
        var services = new ServiceCollection();
        services.AddKomentoAspNetCore().AddSubjectProvider<HeaderSubjectProvider>().AddEnricher<PlanEnricher>();

        var context = new DefaultHttpContext { RequestServices = services.BuildServiceProvider() };
        foreach (var (name, value) in headers) context.Request.Headers[name] = value;
        return context;
    }

    [Test]
    public async Task Resolve_returns_the_subject_and_the_attributes_the_enrichers_built()
    {
        var (subjectId, context) = await KomentoRequestContext.ResolveAsync(
            RequestWith(("x-user", "alice"), ("x-plan", "premium")));

        subjectId.Should().Be("alice");
        context.Attributes["plan"].Should().Be("premium");
    }

    [Test]
    public async Task Resolve_returns_no_subject_when_no_provider_finds_one()
    {
        var (subjectId, context) = await KomentoRequestContext.ResolveAsync(RequestWith(("x-plan", "free")));

        subjectId.Should().BeNull();
        context.Attributes["plan"].Should().Be("free");
    }
}

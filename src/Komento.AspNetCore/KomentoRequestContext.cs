using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Komento.AspNetCore;

/// <summary>Resolves who a request is for and what is known about it, using the registered Komento providers.</summary>
public static class KomentoRequestContext
{
    /// <summary>
    /// Asks the registered <see cref="ISubjectProvider"/>s for the request's subject (the first non-null answer
    /// wins), and runs the registered <see cref="IEvaluationContextEnricher"/>s to build its evaluation context.
    /// </summary>
    /// <returns>The subject ID, or null when no provider found one, and the enriched context.</returns>
    public static async ValueTask<(string? SubjectId, EvaluationContext Context)> ResolveAsync(
        HttpContext httpContext, CancellationToken ct = default)
    {
        var providers = httpContext.RequestServices.GetServices<ISubjectProvider>();
        var enrichers = httpContext.RequestServices.GetServices<IEvaluationContextEnricher>();

        string? subjectId = null;
        foreach (var provider in providers)
        {
            subjectId = provider.GetSubject(httpContext);
            if (subjectId is not null) break;
        }

        var builder = EvaluationContext.Create();
        foreach (var enricher in enrichers)
            await enricher.EnrichAsync(httpContext, builder, ct);

        return (subjectId, builder.Build());
    }
}

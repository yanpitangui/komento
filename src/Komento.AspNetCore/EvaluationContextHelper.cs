using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Komento.AspNetCore;

internal static class EvaluationContextHelper
{
    internal static async ValueTask<(string? SubjectId, EvaluationContext Ctx)> ResolveAsync(
        HttpContext httpContext, CancellationToken ct)
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

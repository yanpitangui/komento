using Komento.AspNetCore;
using Komento.OpenFeature.AspNetCore;
using Microsoft.AspNetCore.Builder;
using OpenFeature;
using OpenFeature.Model;

namespace Komento;

public static class KomentoOpenFeatureContextExtensions
{
    private static readonly object Gate = new();
    private static bool _propagatorSet;

    /// <summary>
    /// Resolves the request's subject and evaluation context once, with the registered
    /// <see cref="ISubjectProvider"/>s and <see cref="IEvaluationContextEnricher"/>s, and sets the result as
    /// OpenFeature's transaction context. Every OpenFeature evaluation in the request then carries the same
    /// targeting key and attributes without building a context per call, and so does every <c>Track</c> call
    /// handled by <c>KomentoFeatureProvider</c> (it applies the transaction context to tracking, which the SDK
    /// leaves out). Place it after authentication. A request with no resolvable subject continues unchanged.
    /// </summary>
    public static IApplicationBuilder UseKomentoOpenFeatureContext(this IApplicationBuilder app)
    {
        EnsureTransactionContextPropagator();

        return app.Use(async (httpContext, next) =>
        {
            var (subjectId, context) = await KomentoRequestContext.ResolveAsync(httpContext, httpContext.RequestAborted);

            if (!string.IsNullOrEmpty(subjectId))
                Api.Instance.SetTransactionContext(ContextMapper.ToOpenFeature(subjectId, context));

            await next();
        });
    }

    // The SDK's default propagator ignores transaction contexts; the async-local one scopes them to the request.
    private static void EnsureTransactionContextPropagator()
    {
        lock (Gate)
        {
            if (_propagatorSet) return;
            Api.Instance.SetTransactionContextPropagator(new AsyncLocalTransactionContextPropagator());
            _propagatorSet = true;
        }
    }
}

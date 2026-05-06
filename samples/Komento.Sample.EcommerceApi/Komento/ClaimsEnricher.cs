using Komento.AspNetCore;
using Microsoft.AspNetCore.Http;

namespace Komento.Sample.EcommerceApi.Komento;

internal sealed class ClaimsEnricher : IEvaluationContextEnricher
{
    public ValueTask EnrichAsync(HttpContext context, EvaluationContextBuilder builder, CancellationToken ct = default)
    {
        var plan = context.User.FindFirst("plan")?.Value;
        if (plan is not null)
            builder.Set("plan", plan);
        return ValueTask.CompletedTask;
    }
}

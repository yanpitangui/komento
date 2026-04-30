using Microsoft.AspNetCore.Http;

namespace Komento.AspNetCore;

public interface IEvaluationContextEnricher
{
    ValueTask EnrichAsync(HttpContext context, EvaluationContextBuilder builder, CancellationToken ct = default);
}

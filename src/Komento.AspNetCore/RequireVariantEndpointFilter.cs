using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Komento.AspNetCore;

internal sealed class RequireVariantEndpointFilter : IEndpointFilter
{
    private readonly string _flagKey;
    private readonly string _variantName;

    public RequireVariantEndpointFilter(string flagKey, string variantName)
    {
        _flagKey     = flagKey;
        _variantName = variantName;
    }

    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext ctx, EndpointFilterDelegate next)
    {
        var ct = ctx.HttpContext.RequestAborted;
        var (subjectId, evalCtx) = await EvaluationContextHelper.ResolveAsync(ctx.HttpContext, ct);

        if (subjectId is null) return Results.NotFound();

        var client = ctx.HttpContext.RequestServices.GetRequiredService<IExperimentClient>();
        var result = await client.GetVariantAsync(_flagKey, subjectId, evalCtx, ct);

        return result != _variantName ? Results.NotFound() : await next(ctx);
    }
}

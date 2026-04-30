using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.DependencyInjection;

namespace Komento.AspNetCore;

[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class)]
public sealed class RequireVariantAttribute : Attribute, IAsyncActionFilter
{
    public string FlagKey     { get; }
    public string VariantName { get; }

    public RequireVariantAttribute(string flagKey, string variantName)
    {
        FlagKey     = flagKey;
        VariantName = variantName;
    }

    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var ct = context.HttpContext.RequestAborted;
        var (subjectId, evalCtx) = await EvaluationContextHelper.ResolveAsync(context.HttpContext, ct);

        if (subjectId is null) { context.Result = new NotFoundResult(); return; }

        var client = context.HttpContext.RequestServices.GetRequiredService<IExperimentClient>();
        var result = await client.GetVariantAsync(FlagKey, subjectId, evalCtx, ct);

        if (result != VariantName) { context.Result = new NotFoundResult(); return; }

        await next();
    }
}

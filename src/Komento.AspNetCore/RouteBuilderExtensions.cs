using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace Komento.AspNetCore;

public static class RouteBuilderExtensions
{
    public static RouteHandlerBuilder RequireVariant(
        this RouteHandlerBuilder builder,
        string flagKey,
        string variantName)
        => (RouteHandlerBuilder)builder.AddEndpointFilter(new RequireVariantEndpointFilter(flagKey, variantName));
}

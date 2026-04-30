using Microsoft.Extensions.DependencyInjection;

namespace Komento.AspNetCore;

public static class ServiceCollectionExtensions
{
    public static KomentoAspNetCoreBuilder AddKomentoAspNetCore(this IServiceCollection services)
        => new(services);
}

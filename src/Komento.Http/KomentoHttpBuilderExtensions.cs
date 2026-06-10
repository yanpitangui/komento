using Microsoft.Extensions.DependencyInjection;

namespace Komento.Http;

public static class KomentoHttpBuilderExtensions
{
    public static KomentoBuilder AddHttpSource<TResponse>(
        this KomentoBuilder                                     builder,
        Func<TResponse, IEnumerable<ExperimentConfig>>          map,
        string                                                  clientName = "Komento.Http")
    {
        builder.Services.AddHttpClient(clientName);
        builder.Services.AddSingleton<IExperimentSource>(sp =>
            new HttpExperimentSource<TResponse>(
                sp.GetRequiredService<IHttpClientFactory>(),
                clientName,
                map));
        return builder;
    }
}

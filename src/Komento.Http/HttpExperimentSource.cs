using System.Net.Http.Json;

namespace Komento.Http;

public sealed class HttpExperimentSource<TResponse> : IExperimentSource
{
    private readonly IHttpClientFactory                          _factory;
    private readonly string                                      _clientName;
    private readonly Func<TResponse, IEnumerable<ExperimentConfig>> _map;

    public HttpExperimentSource(
        IHttpClientFactory                              factory,
        string                                          clientName,
        Func<TResponse, IEnumerable<ExperimentConfig>> map)
    {
        using var probe = factory.CreateClient(clientName);
        if (probe.BaseAddress is null)
            throw new InvalidOperationException(
                $"HttpClient '{clientName}' has no BaseAddress configured. " +
                $"Call services.AddHttpClient(\"{clientName}\", c => c.BaseAddress = ...) " +
                $"before resolving the Komento HTTP source.");

        _factory    = factory;
        _clientName = clientName;
        _map        = map;
    }

    public async ValueTask<IReadOnlyDictionary<string, ExperimentConfig>> LoadAsync(
        IReadOnlySet<string> experimentIds, CancellationToken ct = default)
    {
        var client   = _factory.CreateClient(_clientName);
        var response = await client.GetFromJsonAsync<TResponse>(client.BaseAddress, ct)
                       ?? throw new InvalidOperationException(
                              $"Komento HTTP source '{_clientName}' received a null response.");

        var loadAll = experimentIds.Count == 0;
        var result  = new Dictionary<string, ExperimentConfig>(StringComparer.Ordinal);

        foreach (var config in _map(response))
            if (loadAll || experimentIds.Contains(config.Id))
                result[config.Id] = config;

        return result;
    }
}

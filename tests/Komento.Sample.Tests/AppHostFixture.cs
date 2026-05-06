using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Testing;
using Microsoft.Extensions.DependencyInjection;
using TUnit.Core.Interfaces;

namespace Komento.Sample.Tests;

public sealed class AppHostFixture : IAsyncInitializer, IAsyncDisposable
{
    private DistributedApplication? _app;

    public HttpClient EcommerceClient { get; private set; } = null!;
    public HttpClient AdminClient    { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        var builder = await DistributedApplicationTestingBuilder
            .CreateAsync<Projects.Komento_Sample_AppHost>();

        _app = await builder.BuildAsync();
        await _app.StartAsync();

        var notifications = _app.Services.GetRequiredService<ResourceNotificationService>();

        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        await notifications.WaitForResourceHealthyAsync("ecommerce-api", cts.Token);
        await notifications.WaitForResourceHealthyAsync("admin-api",     cts.Token);

        EcommerceClient = _app.CreateHttpClient("ecommerce-api");
        AdminClient     = _app.CreateHttpClient("admin-api");
    }

    public async ValueTask DisposeAsync()
    {
        EcommerceClient.Dispose();
        AdminClient.Dispose();
        if (_app is not null)
            await _app.DisposeAsync();
    }
}

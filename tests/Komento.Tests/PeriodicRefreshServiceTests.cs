using AwesomeAssertions;
using Komento;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Time.Testing;
using TUnit.Core;

namespace Komento.Tests;

public class PeriodicRefreshServiceTests
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);

    [Test]
    public async Task Refresh_calls_source_and_updates_client_on_each_tick()
    {
        var fake   = new FakeTimeProvider();
        var source = new SignallingSource();

        var provider = BuildProvider(fake, source);
        var host     = provider.GetRequiredService<IHostedService>();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await host.StartAsync(cts.Token);

        fake.Advance(Interval);
        await source.WaitForLoadAsync(cts.Token);

        fake.Advance(Interval);
        await source.WaitForLoadAsync(cts.Token);

        await cts.CancelAsync();
        await host.StopAsync(default);

        source.LoadCount.Should().Be(2);

        var client = provider.GetRequiredService<IExperimentClient>();
        var result = await client.GetVariantAsync("exp-1", "user-1", EvaluationContext.Empty);
        result.IsEligible.Should().BeTrue();
    }

    [Test]
    public async Task Refresh_passes_experimentIds_to_source()
    {
        var fake   = new FakeTimeProvider();
        var source = new SignallingSource();
        var ids    = new HashSet<string>(StringComparer.Ordinal) { "exp-a" };

        var provider = BuildProvider(fake, source, ids);
        var host     = provider.GetRequiredService<IHostedService>();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await host.StartAsync(cts.Token);

        fake.Advance(Interval);
        await source.WaitForLoadAsync(cts.Token);

        await cts.CancelAsync();
        await host.StopAsync(default);

        source.LastRequestedIds.Should().BeEquivalentTo(ids);
    }

    [Test]
    public async Task Refresh_exception_does_not_stop_service()
    {
        var fake   = new FakeTimeProvider();
        var source = new FaultySignallingSource();

        var provider = BuildProvider(fake, source);
        var host     = provider.GetRequiredService<IHostedService>();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await host.StartAsync(cts.Token);

        fake.Advance(Interval);
        await source.WaitForLoadAsync(cts.Token);

        fake.Advance(Interval);
        await source.WaitForLoadAsync(cts.Token);

        await cts.CancelAsync();
        await host.StopAsync(default);

        source.LoadCount.Should().Be(2);
    }

    [Test]
    public void No_TimeProvider_registered_uses_system_clock()
    {
        var services = new ServiceCollection();
        services.AddKomento()
                .AddSource(new SignallingSource())
                .AddPeriodicRefresh(Interval);

        // Should resolve without exception — falls back to TimeProvider.System
        var provider = services.BuildServiceProvider();
        var act = () => provider.GetRequiredService<IHostedService>();
        act.Should().NotThrow();
    }

    private static ServiceProvider BuildProvider(
        FakeTimeProvider   fake,
        IExperimentSource  source,
        IReadOnlySet<string>? ids = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(fake);
        var builder = services.AddKomento().AddSource(source);
        if (ids is not null)
            builder.AddPeriodicRefresh(Interval, ids);
        else
            builder.AddPeriodicRefresh(Interval);
        return services.BuildServiceProvider();
    }

    private static ExperimentConfig MakeConfig(string id) => new()
    {
        Id          = id,
        SubjectType = "user",
        Variants    = [new VariantConfig { Name = "control", Allocation = 1.0 }]
    };

    private sealed class SignallingSource : IExperimentSource
    {
        private readonly SemaphoreSlim _signal = new(0);
        public int LoadCount { get; private set; }
        public IReadOnlySet<string> LastRequestedIds { get; private set; } = new HashSet<string>();

        public ValueTask<IReadOnlyDictionary<string, ExperimentConfig>> LoadAsync(
            IReadOnlySet<string> experimentIds, CancellationToken ct = default)
        {
            LoadCount++;
            LastRequestedIds = experimentIds;
            _signal.Release();
            IReadOnlyDictionary<string, ExperimentConfig> result =
                new Dictionary<string, ExperimentConfig>(StringComparer.Ordinal)
                    { ["exp-1"] = MakeConfig("exp-1") };
            return ValueTask.FromResult(result);
        }

        public Task WaitForLoadAsync(CancellationToken ct) => _signal.WaitAsync(ct);
    }

    private sealed class FaultySignallingSource : IExperimentSource
    {
        private readonly SemaphoreSlim _signal = new(0);
        public int LoadCount { get; private set; }

        public ValueTask<IReadOnlyDictionary<string, ExperimentConfig>> LoadAsync(
            IReadOnlySet<string> experimentIds, CancellationToken ct = default)
        {
            LoadCount++;
            _signal.Release();
            throw new InvalidOperationException("simulated source failure");
        }

        public Task WaitForLoadAsync(CancellationToken ct) => _signal.WaitAsync(ct);
    }
}

using AwesomeAssertions;
using Komento;
using Komento.Internals;
using Komento.OpenFeature;
using OpenFeature.Constant;
using OpenFeature.Model;
using TUnit.Core;

namespace Komento.OpenFeature.Tests;

public class EventsTests
{
    private static ExperimentConfig Config(string id, double control = 0.5) => new()
    {
        Id          = id,
        SubjectType = "user",
        Variants    =
        [
            new VariantConfig { Name = "control",   Allocation = control },
            new VariantConfig { Name = "treatment", Allocation = 1.0 - control }
        ]
    };

    private static async Task<(ExperimentClient Engine, KomentoFeatureProvider Provider, List<ProviderEventPayload> Events)>
        StartAsync()
    {
        var engine   = new ExperimentClient(new KomentoOptions());
        var provider = new KomentoFeatureProvider(engine, updater: engine, changes: engine);
        var domain   = $"events-{Guid.NewGuid():N}";
        await global::OpenFeature.Api.Instance.SetProviderAsync(domain, provider);

        var client = global::OpenFeature.Api.Instance.GetClient(domain);
        var events = new List<ProviderEventPayload>();
        client.AddHandler(ProviderEventTypes.ProviderConfigurationChanged, e => { lock (events) events.Add(e); });
        return (engine, provider, events);
    }

    private static async Task<List<ProviderEventPayload>> WaitForAsync(List<ProviderEventPayload> events, int count)
    {
        using var cts   = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(20));
        try
        {
            while (true)
            {
                lock (events) if (events.Count >= count) return [.. events];
                await timer.WaitForNextTickAsync(cts.Token);
            }
        }
        catch (OperationCanceledException) { lock (events) return [.. events]; }
    }

    [Test]
    public async Task A_config_change_emits_ProviderConfigurationChanged_with_the_changed_flags()
    {
        var (engine, _, events) = await StartAsync();

        await engine.UpdateAsync(Config("events-flag"));

        var received = await WaitForAsync(events, 1);
        var payload  = received.Should().ContainSingle().Subject;
        payload.Type.Should().Be(ProviderEventTypes.ProviderConfigurationChanged);
        payload.ProviderName.Should().Be("Komento");
        payload.FlagsChanged.Should().Equal("events-flag");
    }

    [Test]
    public async Task One_update_that_changes_several_flags_emits_one_event_listing_them_all()
    {
        var (engine, _, events) = await StartAsync();

        await engine.UpdateAsync(new Dictionary<string, ExperimentConfig>
        {
            ["events-a"] = Config("events-a"), ["events-b"] = Config("events-b"), ["events-c"] = Config("events-c")
        });

        var received = await WaitForAsync(events, 1);
        var payload  = received.Should().ContainSingle().Subject;
        payload.FlagsChanged!.OrderBy(f => f).Should().Equal("events-a", "events-b", "events-c");
    }

    [Test]
    public async Task An_update_that_changes_nothing_emits_no_event()
    {
        var (engine, _, events) = await StartAsync();
        await engine.UpdateAsync(Config("events-flag"));
        await WaitForAsync(events, 1);

        await engine.UpdateAsync(Config("events-flag"));    // identical content
        await engine.UpdateAsync(Config("events-flag-2"));  // a real change, used as a marker

        var received = await WaitForAsync(events, 2);
        received.Should().HaveCount(2);
        received[1].FlagsChanged.Should().Equal("events-flag-2");   // nothing between the two real changes
    }

    [Test]
    public async Task Removing_a_flag_emits_an_event_for_it()
    {
        var (engine, _, events) = await StartAsync();
        await engine.UpdateAsync(Config("events-flag"));
        await WaitForAsync(events, 1);

        await engine.RemoveAsync("events-flag");

        var received = await WaitForAsync(events, 2);
        received[1].FlagsChanged.Should().Equal("events-flag");
    }

    [Test]
    public async Task After_shutdown_changes_are_no_longer_announced()
    {
        var (engine, provider, events) = await StartAsync();
        await provider.ShutdownAsync();

        await engine.UpdateAsync(Config("events-after-shutdown"));

        (await WaitForAsync(events, 1)).Should().BeEmpty();
    }
}

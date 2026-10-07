using System.Diagnostics.Metrics;
using AwesomeAssertions;
using Komento.Internals;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using TUnit.Core;

namespace Komento.Tests;

public class TrackTests
{
    private static ExperimentClient Build(Action<KomentoOptions>? configure = null, TimeProvider? time = null)
    {
        var options = new KomentoOptions { EnableTrackStream = true };
        configure?.Invoke(options);
        return new ExperimentClient(options, timeProvider: time);
    }

    private static TrackEvent Read(ExperimentClient client)
    {
        ((ITrackStream)client).Reader.TryRead(out var evt).Should().BeTrue();
        return evt;
    }

    [Test]
    public void Track_records_name_subject_value_and_properties()
    {
        var client = Build();

        client.Track("purchase", "user-1", value: 49.9,
            properties: new Dictionary<string, object?> { ["currency"] = "BRL" });

        var evt = Read(client);
        evt.EventName.Should().Be("purchase");
        evt.SubjectId.Should().Be("user-1");
        evt.Value.Should().Be(49.9);
        evt.Properties.Should().ContainSingle().Which.Should().Be(new KeyValuePair<string, object?>("currency", "BRL"));
    }

    [Test]
    public void Track_does_nothing_when_the_stream_is_disabled()
    {
        var client = Build(o => o.EnableTrackStream = false);

        var act = () => client.Track("purchase", "user-1", value: 1);

        act.Should().NotThrow();
        var reading = () => ((ITrackStream)client).Reader;
        reading.Should().Throw<InvalidOperationException>().WithMessage("*EnableTrackStream*");
    }

    private static EvaluationContext Ctx(params (string Key, object Value)[] attributes)
    {
        var builder = EvaluationContext.Create();
        foreach (var (key, value) in attributes) builder.Set(key, value);
        return builder.Build();
    }

    [Test]
    public void Context_is_the_static_context_plus_the_per_call_context()
    {
        var client = Build(o => o.StaticContext = Ctx(("region", "eu"), ("plan", "free")));

        client.Track("purchase", "user-1", Ctx(("plan", "premium"), ("country", "BR")));

        var context = Read(client).Context!;
        context["region"].Should().Be("eu");        // static only
        context["country"].Should().Be("BR");       // per-call only
        context["plan"].Should().Be("premium");     // per-call wins over static
    }

    [Test]
    public void Context_is_null_when_both_contexts_are_empty()
    {
        var client = Build();

        client.Track("purchase", "user-1");

        Read(client).Context.Should().BeNull();
    }

    [Test]
    public void Context_can_be_excluded()
    {
        var client = Build(o =>
        {
            o.StaticContext               = Ctx(("region", "eu"));
            o.IncludeContextInTrackEvents = false;
        });

        client.Track("purchase", "user-1", Ctx(("plan", "premium")));

        Read(client).Context.Should().BeNull();
    }

    [Test]
    public void Properties_are_a_snapshot_taken_when_Track_is_called()
    {
        var client     = Build();
        var properties = new Dictionary<string, object?> { ["currency"] = "BRL" };

        client.Track("purchase", "user-1", properties: properties);
        properties["currency"] = "USD";   // mutated after the call
        properties["extra"]    = 1;

        var snapshot = Read(client).Properties!;
        snapshot.Should().ContainSingle();
        snapshot["currency"].Should().Be("BRL");
    }

    [Test]
    public void Timestamp_comes_from_the_time_provider()
    {
        var now    = new DateTimeOffset(2026, 3, 4, 5, 6, 7, TimeSpan.Zero);
        var client = Build(time: new FakeTimeProvider(now));

        client.Track("purchase", "user-1");

        Read(client).Timestamp.Should().Be(now);
    }

    [Test]
    public void Track_rejects_a_null_event_name()
    {
        var client = Build();

        var act = () => client.Track(null!, "user-1");

        act.Should().Throw<ArgumentException>();
    }

    [Test]
    public void Track_rejects_an_empty_event_name()
    {
        var client = Build();

        var act = () => client.Track("", "user-1");

        act.Should().Throw<ArgumentException>();
    }

    [Test]
    public void Track_rejects_a_null_subject_id()
    {
        var client = Build();

        var act = () => client.Track("purchase", null!);

        act.Should().Throw<ArgumentException>();
    }

    [Test]
    public void Track_rejects_an_empty_subject_id()
    {
        var client = Build();

        var act = () => client.Track("purchase", "");

        act.Should().Throw<ArgumentException>();
    }

    [Test]
    public void A_full_channel_drops_the_event_and_counts_it()
    {
        long dropped = 0;
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == "Komento" && instrument.Name == "komento.track.dropped")
                l.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((_, value, tags, _) =>
        {
            foreach (var tag in tags)
                if (tag.Key == "reason" && (string?)tag.Value == "queue_full")
                    Interlocked.Add(ref dropped, value);
        });
        listener.Start();

        var client = Build(o => o.TrackChannelCapacity = 1);

        client.Track("a", "user-1");   // fills the channel
        client.Track("b", "user-1");   // dropped

        Interlocked.Read(ref dropped).Should().BeGreaterThanOrEqualTo(1);
        Read(client).EventName.Should().Be("a");
    }

    [Test]
    public void AddKomento_registers_the_tracker_and_the_stream_on_the_same_engine()
    {
        var services = new ServiceCollection();
        services.AddKomento(o => o.EnableTrackStream = true);
        using var provider = services.BuildServiceProvider();

        var tracker = provider.GetRequiredService<IExperimentTracker>();
        var stream  = provider.GetRequiredService<ITrackStream>();
        tracker.Track("purchase", "user-1");

        stream.Reader.TryRead(out var evt).Should().BeTrue();
        evt.EventName.Should().Be("purchase");
    }
}

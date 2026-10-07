using System.Diagnostics.Metrics;
using AwesomeAssertions;
using Komento;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TUnit.Core;

namespace Komento.Sinks.Tests;

public class TrackSinkTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    private static async Task<ServiceProvider> StartAsync(
        Action<KomentoBuilder> configureBuilder, Action<IServiceCollection>? configureServices = null)
    {
        var services = new ServiceCollection();
        configureServices?.Invoke(services);
        configureBuilder(services.AddKomento(o => o.EnableTrackStream = true));
        var provider = services.BuildServiceProvider();

        foreach (var hosted in provider.GetServices<IHostedService>())
            await hosted.StartAsync(CancellationToken.None);
        return provider;
    }

    private static void TrackMany(ServiceProvider provider, int count)
    {
        var tracker = provider.GetRequiredService<IExperimentTracker>();
        for (var i = 0; i < count; i++)
            tracker.Track("purchase", $"user-{i}", value: i);
    }

    [Test]
    public async Task Sink_receives_a_batch_of_conversions_when_batch_size_is_reached()
    {
        var received = new TaskCompletionSource<IReadOnlyList<TrackEvent>>(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var provider = await StartAsync(b => b.AddTrackSink(
            (batch, _) => { received.TrySetResult(batch.ToArray()); return ValueTask.CompletedTask; },
            o => { o.BatchSize = 3; o.FlushInterval = TimeSpan.FromHours(1); }));

        TrackMany(provider, 3);

        var batch = await received.Task.WaitAsync(Timeout);
        batch.Select(e => e.SubjectId).Should().Equal("user-0", "user-1", "user-2");
        batch.Select(e => e.Value).Should().Equal(0.0, 1.0, 2.0);
    }

    [Test]
    public async Task Every_track_sink_receives_every_conversion()
    {
        var a = new TaskCompletionSource<IReadOnlyList<TrackEvent>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var b = new TaskCompletionSource<IReadOnlyList<TrackEvent>>(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var provider = await StartAsync(builder => builder
            .AddTrackSink((batch, _) => { a.TrySetResult(batch.ToArray()); return ValueTask.CompletedTask; },
                o => { o.BatchSize = 2; o.FlushInterval = TimeSpan.FromHours(1); })
            .AddTrackSink((batch, _) => { b.TrySetResult(batch.ToArray()); return ValueTask.CompletedTask; },
                o => { o.BatchSize = 2; o.FlushInterval = TimeSpan.FromHours(1); }));

        TrackMany(provider, 2);

        (await a.Task.WaitAsync(Timeout)).Should().HaveCount(2);
        (await b.Task.WaitAsync(Timeout)).Should().HaveCount(2);
    }

    private sealed class RecordingTrackSink : ITrackSink
    {
        public TaskCompletionSource<IReadOnlyList<TrackEvent>> Received { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ValueTask WriteAsync(IReadOnlyList<TrackEvent> batch, CancellationToken ct)
        {
            Received.TrySetResult(batch.ToArray());
            return ValueTask.CompletedTask;
        }
    }

    [Test]
    public async Task Typed_track_sink_is_resolved_from_the_container()
    {
        var sink = new RecordingTrackSink();

        await using var provider = await StartAsync(
            b => b.AddTrackSink<RecordingTrackSink>(o => { o.BatchSize = 1; o.FlushInterval = TimeSpan.FromHours(1); }),
            services => services.AddSingleton(sink));

        TrackMany(provider, 1);

        (await sink.Received.Task.WaitAsync(Timeout)).Should().ContainSingle();
    }

    [Test]
    public async Task A_blocked_track_sink_counts_its_drops_on_the_track_counter()
    {
        long dropped = 0;
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == "Komento" && instrument.Name == "komento.track.sink.dropped")
                l.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((_, value, tags, _) =>
        {
            foreach (var tag in tags)
                if (tag.Key == "sink" && (string?)tag.Value == "blocked-track-sink")
                    Interlocked.Add(ref dropped, value);
        });
        listener.Start();

        var healthy      = 0;
        var allDelivered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var provider = await StartAsync(builder => builder
            .AddTrackSink(async (_, _) => await Task.Delay(System.Threading.Timeout.Infinite),   // ignores cancellation
                o => { o.Name = "blocked-track-sink"; o.BatchSize = 1; o.MaxPendingBatches = 2; })
            .AddTrackSink((batch, _) =>
                {
                    if (Interlocked.Add(ref healthy, batch.Count) == 20) allDelivered.TrySetResult();
                    return ValueTask.CompletedTask;
                },
                o => { o.Name = "healthy-track-sink"; o.BatchSize = 1; o.FlushInterval = TimeSpan.FromHours(1); }));

        TrackMany(provider, 20);

        await allDelivered.Task.WaitAsync(Timeout);
        Interlocked.Read(ref dropped).Should().BeInRange(17, 18);   // 1 in flight + 2 pending are kept
    }

    [Test]
    public async Task Starting_without_the_track_stream_enabled_fails_with_a_clear_message()
    {
        var services = new ServiceCollection();
        services.AddKomento().AddTrackSink((_, _) => ValueTask.CompletedTask);
        await using var provider = services.BuildServiceProvider();

        var start = async () =>
        {
            foreach (var hosted in provider.GetServices<IHostedService>())
                await hosted.StartAsync(CancellationToken.None);
        };

        await start.Should().ThrowAsync<InvalidOperationException>().WithMessage("*EnableTrackStream*");
    }

    private sealed class CapturingLoggerFactory : ILoggerFactory, ILogger
    {
        public List<string> Messages { get; } = [];
        public TaskCompletionSource Logged { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ILogger CreateLogger(string categoryName) => this;
        public void AddProvider(ILoggerProvider provider) { }
        public void Dispose() { }
        public bool IsEnabled(LogLevel logLevel) => true;
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            lock (Messages) Messages.Add(formatter(state, exception));
            Logged.TrySetResult();
        }
    }

    [Test]
    public async Task Logging_track_sink_writes_one_entry_per_conversion()
    {
        var logs = new CapturingLoggerFactory();

        await using var provider = await StartAsync(
            b => b.AddLoggingTrackSink(o => { o.BatchSize = 1; o.FlushInterval = TimeSpan.FromHours(1); }),
            services => services.AddSingleton<ILoggerFactory>(logs));

        TrackMany(provider, 1);

        await logs.Logged.Task.WaitAsync(Timeout);
        string message;
        lock (logs.Messages) message = logs.Messages.Single();
        message.Should().Contain("purchase").And.Contain("user-0");
    }
}

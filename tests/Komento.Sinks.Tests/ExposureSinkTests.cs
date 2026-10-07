using System.Diagnostics.Metrics;
using AwesomeAssertions;
using Komento;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using TUnit.Core;

namespace Komento.Sinks.Tests;

public class ExposureSinkTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    private static ExperimentConfig OneVariant(string id) => new()
    {
        Id          = id,
        SubjectType = "user",
        Variants    = [new VariantConfig { Name = "only", Allocation = 1.0 }]
    };

    private static async Task<ServiceProvider> StartAsync(
        Action<KomentoBuilder> configureBuilder,
        Action<IServiceCollection>? configureServices = null,
        params ExperimentConfig[] experiments)
    {
        var services = new ServiceCollection();
        configureServices?.Invoke(services);
        configureBuilder(services.AddKomento(o => o.EnableExposureStream = true));
        var provider = services.BuildServiceProvider();

        await provider.GetRequiredService<IConfigUpdater>()
            .UpdateAsync(experiments.ToDictionary(e => e.Id));
        foreach (var hosted in provider.GetServices<IHostedService>())
            await hosted.StartAsync(CancellationToken.None);
        return provider;
    }

    private static async Task EvaluateAsync(ServiceProvider provider, string experiment, int count)
    {
        var client = provider.GetRequiredService<IExperimentClient>();
        for (var i = 0; i < count; i++)
            await client.GetVariantAsync(experiment, $"user-{i}", EvaluationContext.Empty);
    }

    [Test]
    public async Task Sink_receives_a_batch_when_batch_size_is_reached()
    {
        var received = new TaskCompletionSource<IReadOnlyList<ExposureEvent>>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        await using var provider = await StartAsync(
            b => b.AddExposureSink(
                (batch, _) => { received.TrySetResult(batch.ToArray()); return ValueTask.CompletedTask; },
                o => { o.BatchSize = 3; o.FlushInterval = TimeSpan.FromHours(1); }),
            experiments: OneVariant("sink-exp"));

        await EvaluateAsync(provider, "sink-exp", 3);

        var batch = await received.Task.WaitAsync(Timeout);
        batch.Should().HaveCount(3);
        batch.Select(e => e.FlagKey).Should().OnlyContain(k => k == "sink-exp");
    }

    [Test]
    public async Task Partial_batch_is_flushed_after_flush_interval()
    {
        var time     = new FakeTimeProvider();
        var received = new TaskCompletionSource<IReadOnlyList<ExposureEvent>>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        await using var provider = await StartAsync(
            b => b.AddExposureSink(
                (batch, _) => { received.TrySetResult(batch.ToArray()); return ValueTask.CompletedTask; },
                o => { o.BatchSize = 100; o.FlushInterval = TimeSpan.FromSeconds(5); }),
            services => services.AddSingleton<TimeProvider>(time),
            OneVariant("flush-exp"));

        await EvaluateAsync(provider, "flush-exp", 2);

        // The pipeline reads asynchronously, so keep advancing the fake clock until the flush lands.
        // The cancellation token bounds the wait and fails the test (instead of hanging) if it never does.
        using var timeout = new CancellationTokenSource(Timeout);
        while (!received.Task.IsCompleted)
        {
            time.Advance(TimeSpan.FromSeconds(5));
            await Task.Delay(10, timeout.Token);
        }

        (await received.Task.WaitAsync(Timeout)).Should().HaveCount(2);
    }

    [Test]
    public async Task Every_sink_receives_every_exposure()
    {
        var a = new TaskCompletionSource<IReadOnlyList<ExposureEvent>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var b = new TaskCompletionSource<IReadOnlyList<ExposureEvent>>(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var provider = await StartAsync(
            builder => builder
                .AddExposureSink(
                    (batch, _) => { a.TrySetResult(batch.ToArray()); return ValueTask.CompletedTask; },
                    o => { o.BatchSize = 3; o.FlushInterval = TimeSpan.FromHours(1); })
                .AddExposureSink(
                    (batch, _) => { b.TrySetResult(batch.ToArray()); return ValueTask.CompletedTask; },
                    o => { o.BatchSize = 3; o.FlushInterval = TimeSpan.FromHours(1); }),
            experiments: OneVariant("fanout-exp"));

        await EvaluateAsync(provider, "fanout-exp", 3);

        (await a.Task.WaitAsync(Timeout)).Should().HaveCount(3);
        (await b.Task.WaitAsync(Timeout)).Should().HaveCount(3);
    }

    private static long CountDropped(string sinkName, out MeterListener listener, out Func<long> total)
    {
        long sum = 0;
        var l = new MeterListener();
        l.InstrumentPublished = (instrument, ml) =>
        {
            if (instrument.Meter.Name == "Komento" && instrument.Name == "komento.exposures.sink.dropped")
                ml.EnableMeasurementEvents(instrument);
        };
        l.SetMeasurementEventCallback<long>((_, value, tags, _) =>
        {
            foreach (var tag in tags)
                if (tag.Key == "sink" && (string?)tag.Value == sinkName)
                    Interlocked.Add(ref sum, value);
        });
        l.Start();
        listener = l;
        total    = () => Interlocked.Read(ref sum);
        return 0;
    }

    [Test]
    public async Task Blocked_sink_does_not_stop_other_sinks_and_its_drops_are_counted()
    {
        CountDropped("blocked-sink", out var listener, out var dropped);
        using var _ = listener;

        var delivered = 0;
        var allDelivered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var provider = await StartAsync(
            builder => builder
                .AddExposureSink(
                    async (_, _) => await Task.Delay(System.Threading.Timeout.Infinite), // ignores cancellation
                    o => { o.Name = "blocked-sink"; o.BatchSize = 1; o.MaxPendingBatches = 2; })
                .AddExposureSink(
                    (batch, _) =>
                    {
                        if (Interlocked.Add(ref delivered, batch.Count) == 20) allDelivered.TrySetResult();
                        return ValueTask.CompletedTask;
                    },
                    o => { o.Name = "healthy-sink"; o.BatchSize = 1; o.FlushInterval = TimeSpan.FromHours(1); }),
            experiments: OneVariant("blocked-exp"));

        await EvaluateAsync(provider, "blocked-exp", 20);

        await allDelivered.Task.WaitAsync(Timeout);
        // 1 batch in flight + 2 pending are retained; the rest are dropped for this sink only.
        dropped().Should().BeInRange(17, 18);
    }

    [Test]
    public async Task Failing_write_drops_that_batch_and_the_sink_keeps_receiving()
    {
        CountDropped("failing-sink", out var listener, out var dropped);
        using var _ = listener;

        var calls    = 0;
        var received = new TaskCompletionSource<ExposureEvent>(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var provider = await StartAsync(
            builder => builder.AddExposureSink(
                (batch, _) =>
                {
                    if (Interlocked.Increment(ref calls) == 1) throw new InvalidOperationException("boom");
                    received.TrySetResult(batch[0]);
                    return ValueTask.CompletedTask;
                },
                o => { o.Name = "failing-sink"; o.BatchSize = 1; o.FlushInterval = TimeSpan.FromHours(1); }),
            experiments: OneVariant("failing-exp"));

        await EvaluateAsync(provider, "failing-exp", 2);

        (await received.Task.WaitAsync(Timeout)).SubjectId.Should().Be("user-1");
        dropped().Should().Be(1);
    }

    [Test]
    public async Task Write_exceeding_timeout_is_cancelled_and_the_sink_keeps_receiving()
    {
        CountDropped("slow-sink", out var listener, out var dropped);
        using var _ = listener;

        var calls    = 0;
        var received = new TaskCompletionSource<ExposureEvent>(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var provider = await StartAsync(
            builder => builder.AddExposureSink(
                async (batch, ct) =>
                {
                    if (Interlocked.Increment(ref calls) == 1)
                        await Task.Delay(System.Threading.Timeout.Infinite, ct); // honors cancellation
                    received.TrySetResult(batch[0]);
                },
                o =>
                {
                    o.Name = "slow-sink"; o.BatchSize = 1; o.FlushInterval = TimeSpan.FromHours(1);
                    o.WriteTimeout = TimeSpan.FromMilliseconds(100);
                }),
            experiments: OneVariant("slow-exp"));

        await EvaluateAsync(provider, "slow-exp", 2);

        (await received.Task.WaitAsync(Timeout)).SubjectId.Should().Be("user-1");
        dropped().Should().Be(1);
    }

    [Test]
    public async Task Stopping_flushes_the_partial_batch()
    {
        var received = new TaskCompletionSource<IReadOnlyList<ExposureEvent>>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        await using var provider = await StartAsync(
            b => b.AddExposureSink(
                (batch, _) => { received.TrySetResult(batch.ToArray()); return ValueTask.CompletedTask; },
                o => { o.BatchSize = 100; o.FlushInterval = TimeSpan.FromHours(1); }),
            experiments: OneVariant("stop-exp"));

        await EvaluateAsync(provider, "stop-exp", 2);

        foreach (var hosted in provider.GetServices<IHostedService>())
            await hosted.StopAsync(CancellationToken.None);

        (await received.Task.WaitAsync(Timeout)).Should().HaveCount(2);
    }

    [Test]
    public async Task Stopping_does_not_wait_longer_than_the_flush_window_for_a_blocked_sink()
    {
        await using var provider = await StartAsync(
            b => b.AddExposureSink(
                async (_, _) => await Task.Delay(System.Threading.Timeout.Infinite), // ignores cancellation
                o => { o.BatchSize = 1; o.ShutdownFlushTimeout = TimeSpan.FromMilliseconds(200); }),
            experiments: OneVariant("stuck-exp"));

        await EvaluateAsync(provider, "stuck-exp", 3);

        var stop = Task.WhenAll(provider.GetServices<IHostedService>().Select(h => h.StopAsync(CancellationToken.None)));
        await stop.WaitAsync(Timeout); // completes instead of hanging
    }

    private sealed class RecordingSink : IExposureSink
    {
        public TaskCompletionSource<IReadOnlyList<ExposureEvent>> Received { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ValueTask WriteAsync(IReadOnlyList<ExposureEvent> batch, CancellationToken ct)
        {
            Received.TrySetResult(batch.ToArray());
            return ValueTask.CompletedTask;
        }
    }

    [Test]
    public async Task Typed_sink_is_resolved_from_the_container_and_receives_batches()
    {
        var sink = new RecordingSink();

        await using var provider = await StartAsync(
            b => b.AddExposureSink<RecordingSink>(o => { o.BatchSize = 2; o.FlushInterval = TimeSpan.FromHours(1); }),
            services => services.AddSingleton(sink),
            OneVariant("typed-exp"));

        await EvaluateAsync(provider, "typed-exp", 2);

        (await sink.Received.Task.WaitAsync(Timeout)).Should().HaveCount(2);
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
    public async Task Logging_sink_writes_one_structured_entry_per_exposure()
    {
        var logs = new CapturingLoggerFactory();

        await using var provider = await StartAsync(
            b => b.AddLoggingExposureSink(o => { o.BatchSize = 1; o.FlushInterval = TimeSpan.FromHours(1); }),
            services => services.AddSingleton<ILoggerFactory>(logs),
            OneVariant("log-exp"));

        await EvaluateAsync(provider, "log-exp", 1);

        await logs.Logged.Task.WaitAsync(Timeout);
        string message;
        lock (logs.Messages) message = logs.Messages.Single();
        message.Should().Contain("log-exp").And.Contain("user-0").And.Contain("only");
    }

    [Test]
    public async Task Starting_without_the_exposure_stream_enabled_fails_with_a_clear_message()
    {
        var services = new ServiceCollection();
        services.AddKomento().AddExposureSink((_, _) => ValueTask.CompletedTask);
        await using var provider = services.BuildServiceProvider();

        var start = async () =>
        {
            foreach (var hosted in provider.GetServices<IHostedService>())
                await hosted.StartAsync(CancellationToken.None);
        };

        await start.Should().ThrowAsync<InvalidOperationException>().WithMessage("*EnableExposureStream*");
    }
}

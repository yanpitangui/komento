using System.Diagnostics.Metrics;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using TUnit.Core;

namespace Komento.Tests;

public class ExposureStreamTests
{
    private static ExperimentConfig OneVariant(string id) => new()
    {
        Id          = id,
        SubjectType = "user",
        Variants    = [new VariantConfig { Name = "only", Allocation = 1.0 }]
    };

    private static ServiceProvider Build(Action<KomentoOptions>? configure = null, TimeProvider? time = null)
    {
        var services = new ServiceCollection();
        if (time is not null)
            services.AddSingleton(time);
        services.AddKomento(configure);
        return services.BuildServiceProvider();
    }

    [Test]
    public async Task Enabled_stream_delivers_exposures_to_consumers()
    {
        using var provider = Build(o => o.EnableExposureStream = true);
        await provider.GetRequiredService<IConfigUpdater>().UpdateAsync(OneVariant("stream-exp"));

        await provider.GetRequiredService<IExperimentClient>()
            .GetVariantAsync("stream-exp", "user-1", EvaluationContext.Empty);

        var stream = provider.GetRequiredService<IExposureStream>();
        stream.Reader.TryRead(out var exposure).Should().BeTrue();
        exposure.FlagKey.Should().Be("stream-exp");
        exposure.SubjectId.Should().Be("user-1");
    }

    [Test]
    public async Task Exposure_timestamp_comes_from_the_registered_time_provider()
    {
        var now  = new DateTimeOffset(2026, 3, 4, 5, 6, 7, TimeSpan.Zero);
        var time = new FakeTimeProvider(now);
        using var provider = Build(o => o.EnableExposureStream = true, time);
        await provider.GetRequiredService<IConfigUpdater>().UpdateAsync(OneVariant("clock-exp"));

        await provider.GetRequiredService<IExperimentClient>()
            .GetVariantAsync("clock-exp", "user-1", EvaluationContext.Empty);

        provider.GetRequiredService<IExposureStream>().Reader.TryRead(out var exposure).Should().BeTrue();
        exposure.Timestamp.Should().Be(now);
    }

    [Test]
    public void Reader_throws_when_stream_is_disabled()
    {
        using var provider = Build();

        var act = () => provider.GetRequiredService<IExposureStream>().Reader;

        act.Should().Throw<InvalidOperationException>().WithMessage("*EnableExposureStream*");
    }

    [Test]
    public async Task Exposure_dropped_when_channel_full_increments_dropped_counter()
    {
        var dropped = new List<IReadOnlyDictionary<string, object?>>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == "Komento" && instrument.Name == "komento.exposures.dropped")
                l.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((_, _, tags, _) =>
        {
            var dict = new Dictionary<string, object?>();
            foreach (var tag in tags) dict[tag.Key] = tag.Value;
            if ((string?)dict["experiment"] == "drop-exp")
                lock (dropped) dropped.Add(dict);
        });
        listener.Start();

        using var provider = Build(o => { o.EnableExposureStream = true; o.ExposureChannelCapacity = 1; });
        await provider.GetRequiredService<IConfigUpdater>().UpdateAsync(OneVariant("drop-exp"));
        var client = provider.GetRequiredService<IExperimentClient>();

        await client.GetVariantAsync("drop-exp", "user-1", EvaluationContext.Empty); // fills channel
        await client.GetVariantAsync("drop-exp", "user-2", EvaluationContext.Empty); // dropped

        dropped.Should().ContainSingle();
    }

    private static async Task<ExposureEvent> ExposeAsync(ExperimentConfig config)
    {
        using var provider = Build(o => o.EnableExposureStream = true);
        await provider.GetRequiredService<IConfigUpdater>().UpdateAsync(config);
        await provider.GetRequiredService<IExperimentClient>()
            .GetVariantAsync(config.Id, "user-1", EvaluationContext.Empty);
        provider.GetRequiredService<IExposureStream>().Reader.TryRead(out var exposure).Should().BeTrue();
        return exposure;
    }

    [Test]
    public async Task Exposure_carries_the_experiments_subject_type()
    {
        var exposure = await ExposeAsync(new ExperimentConfig
        {
            Id          = "subject-type-exp",
            SubjectType = "device",
            Variants    = [new VariantConfig { Name = "only", Allocation = 1.0 }]
        });

        exposure.SubjectType.Should().Be("device");
    }
}

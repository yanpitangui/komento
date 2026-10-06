using System.Diagnostics.Metrics;
using AwesomeAssertions;
using Komento.Internals;
using TUnit.Core;

namespace Komento.Tests;

public class ExposureMetricsTests
{
    private sealed record Measurement(long Value, IReadOnlyDictionary<string, object?> Tags);

    private static ExperimentClient BuildClient(ExperimentConfig config)
    {
        var client = new ExperimentClient(new KomentoOptions());
        client.UpdateAsync(config).AsTask().Wait();
        return client;
    }

    private static List<Measurement> Listen(string experimentId, out MeterListener listener)
    {
        var measurements = new List<Measurement>();
        var l = new MeterListener();
        l.InstrumentPublished = (instrument, ml) =>
        {
            if (instrument.Meter.Name == "Komento" && instrument.Name == "komento.exposures")
                ml.EnableMeasurementEvents(instrument);
        };
        l.SetMeasurementEventCallback<long>((_, value, tags, _) =>
        {
            var dict = new Dictionary<string, object?>();
            foreach (var tag in tags) dict[tag.Key] = tag.Value;
            if (dict.TryGetValue("experiment", out var e) && (string?)e == experimentId)
                lock (measurements) measurements.Add(new Measurement(value, dict));
        });
        l.Start();
        listener = l;
        return measurements;
    }

    [Test]
    public async Task Assigned_exposure_increments_counter_tagged_with_experiment_and_variant()
    {
        var client = BuildClient(new ExperimentConfig
        {
            Id          = "metrics-assigned",
            SubjectType = "user",
            Variants    = [new VariantConfig { Name = "only", Allocation = 1.0 }]
        });
        var measurements = Listen("metrics-assigned", out var listener);
        using var _ = listener;

        await client.GetVariantAsync("metrics-assigned", "user-1", EvaluationContext.Empty);

        measurements.Should().ContainSingle();
        measurements[0].Value.Should().Be(1);
        measurements[0].Tags["variant"].Should().Be("only");
        measurements[0].Tags["outcome"].Should().Be("assigned");
    }

    [Test]
    public async Task Outsider_exposure_is_tagged_outsider()
    {
        var client = BuildClient(new ExperimentConfig
        {
            Id          = "metrics-outsider",
            SubjectType = "user",
            Variants    = [new VariantConfig { Name = "none", Allocation = 0.0 }]
        });
        var measurements = Listen("metrics-outsider", out var listener);
        using var _ = listener;

        await client.GetVariantAsync("metrics-outsider", "user-1", EvaluationContext.Empty);

        measurements.Should().ContainSingle();
        measurements[0].Tags["outcome"].Should().Be("outsider");
    }

    [Test]
    public async Task Ineligible_exposure_is_tagged_ineligible()
    {
        var client = BuildClient(new ExperimentConfig
        {
            Id          = "metrics-ineligible",
            SubjectType = "user",
            Variants    = [new VariantConfig { Name = "only", Allocation = 1.0 }],
            GlobalFilters = [new TraitEqualsFilter { Key = "country", Value = "BR" }]
        });
        var measurements = Listen("metrics-ineligible", out var listener);
        using var _ = listener;

        await client.GetVariantAsync("metrics-ineligible", "user-1", EvaluationContext.Empty);

        measurements.Should().ContainSingle();
        measurements[0].Tags["outcome"].Should().Be("ineligible");
    }
}

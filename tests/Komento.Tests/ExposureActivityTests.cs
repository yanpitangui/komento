using System.Diagnostics;
using AwesomeAssertions;
using Komento.Internals;
using TUnit.Core;

namespace Komento.Tests;

public class ExposureActivityTests
{
    private static ExperimentConfig OneVariant(string id) => new()
    {
        Id          = id,
        SubjectType = "device",
        Variants    = [new VariantConfig { Name = "only", Allocation = 1.0 }]
    };

    private static ExperimentClient BuildClient(ExperimentConfig config, Action<KomentoOptions>? configure = null)
    {
        var options = new KomentoOptions();
        configure?.Invoke(options);
        var client = new ExperimentClient(options);
        client.UpdateAsync(config).AsTask().Wait();
        return client;
    }

    /// <summary>A uniquely named source and listener per test, so parallel tests cannot affect each other's sampling.</summary>
    private sealed class Trace : IDisposable
    {
        private readonly ActivitySource   _source   = new($"Komento.Tests.Activity.{Guid.NewGuid():N}");
        private readonly ActivityListener _listener;

        public Trace()
        {
            var name = _source.Name;
            _listener = new ActivityListener
            {
                ShouldListenTo = source => source.Name == name,
                Sample         = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded
            };
            ActivitySource.AddActivityListener(_listener);
        }

        public Activity StartRequest() => _source.StartActivity("request")!;

        public void Dispose() { _listener.Dispose(); _source.Dispose(); }
    }

    private static Dictionary<string, object?> Tags(ActivityEvent e)
        => e.Tags.ToDictionary(t => t.Key, t => t.Value);

    [Test]
    public async Task Exposure_is_recorded_as_a_feature_flag_evaluation_event_on_the_current_activity()
    {
        using var trace = new Trace();
        var client         = BuildClient(OneVariant("activity-exp"));

        using var activity = trace.StartRequest();
        await client.GetVariantAsync("activity-exp", "device-1", EvaluationContext.Empty);

        var evaluation = activity.Events.Should().ContainSingle(e => e.Name == "feature_flag.evaluation").Subject;
        var tags       = Tags(evaluation);
        tags["feature_flag.key"].Should().Be("activity-exp");
        tags["feature_flag.result.variant"].Should().Be("only");
        tags["feature_flag.provider.name"].Should().Be("Komento");
        tags["komento.outcome"].Should().Be("assigned");
        tags["komento.subject_type"].Should().Be("device");
    }

    [Test]
    public async Task Subject_id_is_recorded_by_default()
    {
        using var trace    = new Trace();
        var client         = BuildClient(OneVariant("activity-id"));

        using var activity = trace.StartRequest();
        await client.GetVariantAsync("activity-id", "device-1", EvaluationContext.Empty);

        Tags(activity.Events.Single())["feature_flag.context.id"].Should().Be("device-1");
    }

    [Test]
    public async Task Subject_id_can_be_excluded()
    {
        using var trace    = new Trace();
        var client         = BuildClient(OneVariant("activity-no-id"), o => o.IncludeSubjectIdInActivityEvents = false);

        using var activity = trace.StartRequest();
        await client.GetVariantAsync("activity-no-id", "device-1", EvaluationContext.Empty);

        Tags(activity.Events.Single()).Should().NotContainKey("feature_flag.context.id");
    }

    [Test]
    public async Task Activity_events_can_be_turned_off()
    {
        using var trace = new Trace();
        var client         = BuildClient(OneVariant("activity-off"), o => o.EmitActivityEvents = false);

        using var activity = trace.StartRequest();
        await client.GetVariantAsync("activity-off", "device-1", EvaluationContext.Empty);

        activity.Events.Should().BeEmpty();
    }

    [Test]
    public async Task No_event_is_recorded_when_the_trace_is_not_recording()
    {
        var client = BuildClient(OneVariant("activity-unsampled"));

        // Created directly: the test host's own listeners would otherwise force sampling on.
        var activity = new Activity("request") { IsAllDataRequested = false };
        activity.Start();
        try
        {
            await client.GetVariantAsync("activity-unsampled", "device-1", EvaluationContext.Empty);

            activity.Events.Should().BeEmpty();
        }
        finally { activity.Stop(); }
    }

    [Test]
    public async Task Evaluating_without_an_ambient_activity_does_not_throw()
    {
        Activity.Current = null;
        var client       = BuildClient(OneVariant("activity-none"));

        var act = async () => await client.GetVariantAsync("activity-none", "device-1", EvaluationContext.Empty);

        await act.Should().NotThrowAsync();
    }
}

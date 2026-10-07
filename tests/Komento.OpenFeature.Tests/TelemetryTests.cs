using System.Diagnostics;
using AwesomeAssertions;
using Komento;
using Komento.Internals;
using Komento.OpenFeature;
using OpenFeature.Hooks;
using TUnit.Core;
using OFContext = OpenFeature.Model.EvaluationContext;

namespace Komento.OpenFeature.Tests;

public class TelemetryTests
{
    /// <summary>
    /// Counts the feature_flag.evaluation events recorded on the request span started here, or on any span
    /// beneath it (the SDK may start its own), while <paramref name="act"/> runs. Spans from tests running in
    /// parallel are ignored.
    /// </summary>
    private static async Task<int> EvaluationEventsDuring(Func<Task> act)
    {
        var source  = new ActivitySource($"Komento.Tests.Telemetry.{Guid.NewGuid():N}");
        var count   = 0;
        Activity? request = null;

        static bool IsSelfOrDescendantOf(Activity? activity, Activity? ancestor)
        {
            for (; activity is not null; activity = activity.Parent)
                if (ReferenceEquals(activity, ancestor)) return true;
            return false;
        }

        using var listener = new ActivityListener
        {
            ShouldListenTo  = _ => true,
            Sample          = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = a =>
            {
                if (IsSelfOrDescendantOf(a, Volatile.Read(ref request)))
                    Interlocked.Add(ref count, a.Events.Count(e => e.Name == "feature_flag.evaluation"));
            }
        };
        ActivitySource.AddActivityListener(listener);

        using (var started = source.StartActivity("request"))
        {
            Volatile.Write(ref request, started);
            await act();
        }
        source.Dispose();

        return Volatile.Read(ref count);
    }

    private static async Task<global::OpenFeature.FeatureClient> StartAsync(bool komentoEmitsEvents)
    {
        var engine = new ExperimentClient(new KomentoOptions { EmitActivityEvents = komentoEmitsEvents });
        await engine.UpdateAsync(new ExperimentConfig
        {
            Id          = "telemetry-flag",
            SubjectType = "user",
            Variants    = [new VariantConfig { Name = "on", Allocation = 1.0, Value = true }]
        });
        var domain = $"telemetry-{Guid.NewGuid():N}";
        await global::OpenFeature.Api.Instance.SetProviderAsync(domain, new KomentoFeatureProvider(engine));
        return global::OpenFeature.Api.Instance.GetClient(domain);
    }

    private static readonly OFContext Subject = OFContext.Builder().SetTargetingKey("user-1").Build();

    [Test]
    public async Task An_OpenFeature_evaluation_records_one_event_from_Komento_by_default()
    {
        var client = await StartAsync(komentoEmitsEvents: true);

        var events = await EvaluationEventsDuring(async () =>
            await client.GetBooleanValueAsync("telemetry-flag", false, Subject));

        events.Should().Be(1);
    }

    [Test]
    public async Task Using_both_Komentos_event_and_the_SDKs_trace_hook_records_the_event_twice()
    {
        // The documented rule: pick one. This pins down what happens when both are on.
        var client = await StartAsync(komentoEmitsEvents: true);
        client.AddHooks(new TraceEnricherHook(TraceEnricherHookOptions.CreateBuilder().Build()));

        var events = await EvaluationEventsDuring(async () =>
            await client.GetBooleanValueAsync("telemetry-flag", false, Subject));

        events.Should().Be(2);
    }

    [Test]
    public async Task Using_the_SDKs_trace_hook_instead_records_one_event_when_Komentos_is_turned_off()
    {
        var client = await StartAsync(komentoEmitsEvents: false);
        client.AddHooks(new TraceEnricherHook(TraceEnricherHookOptions.CreateBuilder().Build()));

        var events = await EvaluationEventsDuring(async () =>
            await client.GetBooleanValueAsync("telemetry-flag", false, Subject));

        events.Should().Be(1);
    }
}

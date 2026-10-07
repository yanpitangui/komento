using AwesomeAssertions;
using Komento;
using Komento.Internals;
using Komento.OpenFeature;
using OpenFeature.Model;
using TUnit.Core;
using KomentoContext = Komento.EvaluationContext;
using OFContext = OpenFeature.Model.EvaluationContext;

namespace Komento.OpenFeature.Tests;

public class TrackTests
{
    private sealed record Call(string EventName, string SubjectId, KomentoContext Context, double? Value,
                               IReadOnlyDictionary<string, object?>? Properties);

    private sealed class RecordingTracker : IExperimentTracker
    {
        public List<Call> Calls { get; } = [];

        public void Track(string eventName, string subjectId, in KomentoContext ctx,
                          double? value = null, IReadOnlyDictionary<string, object?>? properties = null)
            => Calls.Add(new Call(eventName, subjectId, ctx, value, properties));

        public void Track(string eventName, string subjectId,
                          double? value = null, IReadOnlyDictionary<string, object?>? properties = null)
            => Calls.Add(new Call(eventName, subjectId, KomentoContext.Empty, value, properties));
    }

    private static (KomentoFeatureProvider Provider, RecordingTracker Tracker) Build()
    {
        var tracker = new RecordingTracker();
        var client  = new ExperimentClient(new KomentoOptions());
        return (new KomentoFeatureProvider(client, tracker), tracker);
    }

    private static OFContext Subject(string id) => OFContext.Builder().SetTargetingKey(id).Build();

    [Test]
    public void The_SDK_keeps_the_numeric_value_in_a_Value_field_apart_from_the_other_fields()
    {
        // Documents the SDK behavior the provider relies on to find the numeric value.
        var details = TrackingEventDetails.Builder().SetValue(49.9).Set("currency", "BRL").Build();

        details.Value.Should().Be(49.9);
        details.AsDictionary().Keys.Should().Equal("currency");
    }

    [Test]
    public void Track_forwards_name_subject_value_and_fields_to_the_tracker()
    {
        var (provider, tracker) = Build();
        var details = TrackingEventDetails.Builder()
            .SetValue(49.9).Set("currency", "BRL").Set("items", 3).Set("gift", true).Build();

        provider.Track("purchase", Subject("user-1"), details);

        var call = tracker.Calls.Should().ContainSingle().Subject;
        call.EventName.Should().Be("purchase");
        call.SubjectId.Should().Be("user-1");
        call.Value.Should().Be(49.9);
        call.Properties!["currency"].Should().Be("BRL");
        call.Properties["items"].Should().Be(3.0);     // numbers arrive as double
        call.Properties["gift"].Should().Be(true);
        call.Properties.Should().NotContainKey("value");
    }

    [Test]
    public void Track_converts_the_evaluation_context_like_an_evaluation_does()
    {
        var (provider, tracker) = Build();
        var context = OFContext.Builder().SetTargetingKey("user-1")
            .Set("plan", new Value("premium")).Set("beta", new Value(true)).Build();

        provider.Track("purchase", context, TrackingEventDetails.Empty);

        var ctx = tracker.Calls.Single().Context;
        ctx.TryGetValue("plan", out var plan).Should().BeTrue();
        plan.Should().Be("premium");
        ctx.TryGetValue("beta", out var beta).Should().BeTrue();
        beta.Should().Be(true);
        ctx.TryGetValue("targetingKey", out _).Should().BeFalse();
    }

    [Test]
    public void Track_without_details_still_records_the_conversion()
    {
        var (provider, tracker) = Build();

        provider.Track("signup", Subject("user-1"), null);

        var call = tracker.Calls.Should().ContainSingle().Subject;
        call.Value.Should().BeNull();
        call.Properties.Should().BeNull();
    }

    [Test]
    public void Track_without_a_targeting_key_is_skipped()
    {
        var (provider, tracker) = Build();

        provider.Track("purchase", OFContext.Builder().Build(), TrackingEventDetails.Empty);

        tracker.Calls.Should().BeEmpty();
    }

    [Test]
    public void Track_without_a_tracker_does_nothing()
    {
        var provider = new KomentoFeatureProvider(new ExperimentClient(new KomentoOptions()));

        var act = () => provider.Track("purchase", Subject("user-1"), TrackingEventDetails.Empty);

        act.Should().NotThrow();
    }

    [Test]
    [NotInParallel] // sets the process-wide global context
    public async Task The_SDK_merges_the_global_context_into_Track()
    {
        var (provider, tracker) = Build();
        var domain = $"track-domain-{Guid.NewGuid():N}";
        await global::OpenFeature.Api.Instance.SetProviderAsync(domain, provider);
        global::OpenFeature.Api.Instance.SetContext(OFContext.Builder().Set("region", new Value("eu")).Build());
        try
        {
            var client = global::OpenFeature.Api.Instance.GetClient(domain);
            client.Track("purchase", Subject("user-1"), TrackingEventDetails.Empty);

            var ctx = tracker.Calls.Should().ContainSingle().Subject.Context;
            ctx.TryGetValue("region", out var region).Should().BeTrue();
            region.Should().Be("eu");
        }
        finally
        {
            global::OpenFeature.Api.Instance.SetContext(OFContext.Empty);
        }
    }
}

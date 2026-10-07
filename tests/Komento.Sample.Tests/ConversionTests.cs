using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using AwesomeAssertions;
using Komento.Sample.Contracts;
using TUnit.Core;

namespace Komento.Sample.Tests;

[ClassDataSource<AppHostFixture>(Shared = SharedType.PerTestSession)]
public sealed class ConversionTests(AppHostFixture fixture)
{
    [Test]
    public async Task Native_purchase_is_recorded_with_value_properties_and_merged_context()
    {
        var userId = $"conversion-user-{Guid.NewGuid():N}";
        var token  = await GetTokenAsync(userId, plan: "premium");

        using var request = Authorized(HttpMethod.Post, "/products/42/purchase", token);
        request.Content = JsonContent.Create(new PurchaseRequest(49.90m));
        var response = await fixture.EcommerceClient.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadFromJsonAsync<PurchaseResponse>())!.Amount.Should().Be(49.90m);

        var conversion = (await WaitForConversionsAsync(userId, expected: 1)).Single();

        conversion.EventName.Should().Be("purchase");
        conversion.Value.Should().Be(49.90);
        conversion.Properties!["productId"].Should().Be("42");
        conversion.Context!["plan"].Should().Be("premium");              // per-call context
        conversion.Context["service"].Should().Be("ecommerce-api");      // KomentoOptions.StaticContext
    }

    [Test]
    public async Task OpenFeature_click_is_recorded_through_the_same_pipeline()
    {
        var userId = $"conversion-user-{Guid.NewGuid():N}";
        var token  = await GetTokenAsync(userId, plan: "free");

        using var request = Authorized(HttpMethod.Post, "/recommendations/clicks", token);
        request.Content = JsonContent.Create(new RecommendationClickRequest("Widget A"));
        (await fixture.EcommerceClient.SendAsync(request)).StatusCode.Should().Be(HttpStatusCode.NoContent);

        var conversion = (await WaitForConversionsAsync(userId, expected: 1)).Single();

        conversion.EventName.Should().Be("recommendation-click");
        conversion.Properties!["item"].Should().Be("Widget A");
        conversion.Context!["plan"].Should().Be("free");
        conversion.Context["service"].Should().Be("ecommerce-api");
    }

    [Test]
    public async Task Conversions_endpoint_rejects_a_blank_subject_id()
    {
        var response = await fixture.AdminClient.GetAsync("/conversions?subjectId=%20");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    private static HttpRequestMessage Authorized(HttpMethod method, string path, string token)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return request;
    }

    private async Task<string> GetTokenAsync(string userId, string plan)
    {
        var response = await fixture.EcommerceClient.GetAsync(
            $"/token?userId={Uri.EscapeDataString(userId)}&plan={Uri.EscapeDataString(plan)}");
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<TokenResponse>())!.Token;
    }

    private async Task<IReadOnlyList<ConversionResponse>> GetConversionsAsync(string subjectId, CancellationToken ct)
    {
        var response = await fixture.AdminClient.GetAsync($"/conversions?subjectId={Uri.EscapeDataString(subjectId)}", ct);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<ConversionResponse[]>(ct))!;
    }

    /// <summary>
    /// The APIs run as separate processes, so conversions arrive after real I/O (the sink's batch flush);
    /// poll until enough have arrived or the timeout elapses.
    /// </summary>
    private async Task<IReadOnlyList<ConversionResponse>> WaitForConversionsAsync(string subjectId, int expected)
    {
        using var cts   = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(500));
        IReadOnlyList<ConversionResponse> conversions = [];
        try
        {
            do
            {
                conversions = await GetConversionsAsync(subjectId, cts.Token);
                if (conversions.Count >= expected) return conversions;
            }
            while (await timer.WaitForNextTickAsync(cts.Token));
        }
        catch (OperationCanceledException) { }

        return conversions;
    }
}

using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using AwesomeAssertions;
using Komento.Sample.Contracts;
using TUnit.Core;

namespace Komento.Sample.Tests;

[ClassDataSource<AppHostFixture>(Shared = SharedType.PerTestSession)]
public sealed class ExposureTests(AppHostFixture fixture)
{
    [Test]
    public async Task Viewing_a_product_records_an_exposure_in_postgres()
    {
        var userId = $"exposure-user-{Guid.NewGuid():N}";
        var token  = await GetTokenAsync(userId);

        await ViewProductAsync(token);

        // Exposures are batched, so they arrive after the sink's flush interval.
        // /products/{id} evaluates two experiments; a free-plan user who is neither VIP nor loyalty
        // is ineligible for both. (The sample README documents exactly this.)
        var exposures = await WaitForExposuresAsync(userId, expected: 2, TimeSpan.FromSeconds(30));

        // Newest first: /products/{id} evaluates premium-product-page, then price-display.
        exposures.Select(e => e.Experiment)
            .Should().Equal("price-display", "premium-product-page");
        exposures.Should().OnlyContain(e =>
            e.SubjectId == userId &&
            e.SubjectType == "user" &&
            e.Variant == "control" &&
            !e.IsEligible &&
            !e.IsOutsider);
    }

    [Test]
    public async Task Exposures_endpoint_rejects_a_blank_subject_id()
    {
        var response = await fixture.AdminClient.GetAsync("/exposures?subjectId=%20");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Test]
    public async Task Exposures_endpoint_returns_at_most_the_latest_100()
    {
        var userId = $"exposure-user-{Guid.NewGuid():N}";
        var token  = await GetTokenAsync(userId);

        // Each product view evaluates two experiments, so 51 views record 102 exposures.
        for (var i = 0; i < 51; i++)
            await ViewProductAsync(token);

        // The sink writes in order, so once a later marker subject's exposures are visible,
        // all of the subject's exposures above have been written too.
        var markerId = $"exposure-marker-{Guid.NewGuid():N}";
        await ViewProductAsync(await GetTokenAsync(markerId));
        await WaitForExposuresAsync(markerId, expected: 2, TimeSpan.FromSeconds(30));

        var exposures = await GetExposuresAsync(userId);

        exposures.Should().HaveCount(100);
    }

    private async Task ViewProductAsync(string token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/products/42");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        (await fixture.EcommerceClient.SendAsync(request)).EnsureSuccessStatusCode();
    }

    private async Task<string> GetTokenAsync(string userId)
    {
        var response = await fixture.EcommerceClient.GetAsync($"/token?userId={Uri.EscapeDataString(userId)}");
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<TokenResponse>();
        return body!.Token;
    }

    private async Task<IReadOnlyList<ExposureResponse>> GetExposuresAsync(string subjectId, CancellationToken ct = default)
    {
        var response = await fixture.AdminClient.GetAsync($"/exposures?subjectId={Uri.EscapeDataString(subjectId)}", ct);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<ExposureResponse[]>(ct))!;
    }

    /// <summary>
    /// The APIs run as separate processes, so exposures arrive after real I/O (the sink's batch flush)
    /// and there is no clock to fake; poll until enough have arrived or the timeout elapses.
    /// </summary>
    private async Task<IReadOnlyList<ExposureResponse>> WaitForExposuresAsync(
        string subjectId, int expected, TimeSpan timeout)
    {
        using var cts   = new CancellationTokenSource(timeout);
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(500));
        IReadOnlyList<ExposureResponse> exposures = [];
        try
        {
            do
            {
                exposures = await GetExposuresAsync(subjectId, cts.Token);
                if (exposures.Count >= expected) return exposures;
            }
            while (await timer.WaitForNextTickAsync(cts.Token));
        }
        catch (OperationCanceledException) { }

        return exposures;
    }
}

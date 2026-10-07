using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using AwesomeAssertions;
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

        using var request = new HttpRequestMessage(HttpMethod.Get, "/products/42");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        (await fixture.EcommerceClient.SendAsync(request)).EnsureSuccessStatusCode();

        // Exposures are batched, so they arrive after the sink's flush interval.
        // /products/{id} evaluates two experiments; a free-plan user who is neither VIP nor loyalty
        // is ineligible for both. (The sample README documents exactly this.)
        var rows = await PollAsync(userId, expectedRows: 2, TimeSpan.FromSeconds(30));

        rows.Select(r => r.GetProperty("experiment").GetString())
            .Should().BeEquivalentTo(["premium-product-page", "price-display"]);
        rows.Should().OnlyContain(r =>
            r.GetProperty("subjectType").GetString() == "user" &&
            r.GetProperty("variant").GetString() == "control" &&
            !r.GetProperty("isEligible").GetBoolean() &&
            !r.GetProperty("isOutsider").GetBoolean());
    }

    private async Task<string> GetTokenAsync(string userId)
    {
        var response = await fixture.EcommerceClient.GetAsync($"/token?userId={userId}");
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("token").GetString()!;
    }

    private async Task<JsonElement[]> PollAsync(string subjectId, int expectedRows, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        JsonElement[] rows = [];
        while (DateTime.UtcNow < deadline)
        {
            var response = await fixture.AdminClient.GetAsync($"/exposures?subjectId={subjectId}");
            if (response.IsSuccessStatusCode)
            {
                rows = (await response.Content.ReadFromJsonAsync<JsonElement[]>())!;
                if (rows.Length >= expectedRows) return rows;
            }
            await Task.Delay(500);
        }
        return rows;
    }
}

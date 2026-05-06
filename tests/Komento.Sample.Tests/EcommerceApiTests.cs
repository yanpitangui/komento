using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using AwesomeAssertions;
using TUnit.Core;

namespace Komento.Sample.Tests;

[ClassDataSource<AppHostFixture>(Shared = SharedType.PerTestSession)]
public sealed class EcommerceApiTests(AppHostFixture fixture)
{
    // ── Token endpoint ─────────────────────────────────────────────────────

    [Test]
    public async Task GetToken_ReturnsJwt()
    {
        var response = await fixture.EcommerceClient.GetAsync("/token?userId=alice");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("token").GetString().Should().NotBeNullOrEmpty();
    }

    // ── Authorization ──────────────────────────────────────────────────────

    [Test]
    public async Task GetProduct_WithoutToken_ReturnsUnauthorized()
    {
        var response = await fixture.EcommerceClient.GetAsync("/products/42");
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Test]
    public async Task GetRecommendations_WithoutToken_ReturnsUnauthorized()
    {
        var response = await fixture.EcommerceClient.GetAsync("/recommendations");
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // ── price-display experiment ───────────────────────────────────────────

    [Test]
    public async Task GetProduct_AsRegularUser_ReturnsDefaultPrice()
    {
        // "nobody" is neither VIP nor loyalty — price-display filters them out
        var token = await GetTokenAsync("nobody");
        var body  = await GetProductAsync("42", token);

        body.GetProperty("priceVariant").GetString().Should().Be("default");
        body.GetProperty("price").GetDecimal().Should().Be(99.99m);
    }

    [Test]
    public async Task GetProduct_AsVipOnlyUser_ReturnsVipPrice()
    {
        // user-3 is seeded as VIP (postgres) but not loyalty
        var token = await GetTokenAsync("user-3");
        var body  = await GetProductAsync("1", token);

        body.GetProperty("priceVariant").GetString().Should().Be("vip-price");
        body.GetProperty("price").GetDecimal().Should().Be(89.99m);
    }

    [Test]
    public async Task GetProduct_AsVipAndLoyaltyUser_ReturnsLoyaltyPrice()
    {
        // user-1 is seeded as both VIP (postgres) and loyalty (NATS KV)
        var token = await GetTokenAsync("user-1");
        var body  = await GetProductAsync("1", token);

        body.GetProperty("priceVariant").GetString().Should().Be("loyalty-price");
        body.GetProperty("price").GetDecimal().Should().Be(79.99m);
    }

    // ── premium-product-page experiment ───────────────────────────────────

    [Test]
    public async Task GetProduct_AsFreeTierUser_ReturnsNoPremiumPage()
    {
        var token = await GetTokenAsync("alice", plan: "free");
        var body  = await GetProductAsync("42", token);

        body.GetProperty("premiumPage").GetBoolean().Should().BeFalse();
    }

    [Test]
    public async Task GetProduct_AsPremiumUser_ReturnsPremiumPage()
    {
        var token = await GetTokenAsync("alice", plan: "premium");
        var body  = await GetProductAsync("42", token);

        body.GetProperty("premiumPage").GetBoolean().Should().BeTrue();
    }

    // ── recommendation-algorithm experiment (OpenFeature) ─────────────────

    [Test]
    public async Task GetRecommendations_ReturnsValidAlgorithm()
    {
        var token    = await GetTokenAsync("alice");
        var response = await AuthGet("/recommendations", token);

        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("algorithm").GetString()
            .Should().BeOneOf("collaborative", "content-based");
    }

    // ── Admin → EcommerceApi propagation ──────────────────────────────────

    [Test]
    public async Task Admin_AddLoyaltyMember_UserGetsLoyaltyPriceImmediately()
    {
        // user-4 is VIP (seeded in postgres) but NOT loyalty initially
        const string userId = "user-4";

        var putResponse = await fixture.AdminClient.PutAsync($"/loyalty/{userId}", null);
        putResponse.EnsureSuccessStatusCode();

        // NatsLoyaltyStore does a point-GET per request — immediately consistent
        var token = await GetTokenAsync(userId);
        var body  = await GetProductAsync("1", token);

        body.GetProperty("priceVariant").GetString().Should().Be("loyalty-price");

        // Clean up
        await fixture.AdminClient.DeleteAsync($"/loyalty/{userId}");
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    private async Task<string> GetTokenAsync(string userId, string plan = "free")
    {
        var response = await fixture.EcommerceClient
            .GetAsync($"/token?userId={userId}&plan={plan}");
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("token").GetString()!;
    }

    private async Task<JsonElement> GetProductAsync(string productId, string token)
    {
        var response = await AuthGet($"/products/{productId}", token);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private Task<HttpResponseMessage> AuthGet(string path, string token)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return fixture.EcommerceClient.SendAsync(request);
    }
}

using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using AwesomeAssertions;
using Komento.Sample.Contracts;
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
        var body = await response.Content.ReadFromJsonAsync<TokenResponse>();
        body!.Token.Should().NotBeNullOrEmpty();
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
        var token   = await GetTokenAsync("nobody");
        var product = await GetProductAsync("42", token);

        product.PriceVariant.Should().Be("default");
        product.Price.Should().Be(99.99m);
    }

    [Test]
    public async Task GetProduct_AsVipOnlyUser_ReturnsVipPrice()
    {
        // user-3 is seeded as VIP (postgres) but not loyalty
        var token   = await GetTokenAsync("user-3");
        var product = await GetProductAsync("1", token);

        product.PriceVariant.Should().Be("vip-price");
        product.Price.Should().Be(89.99m);
    }

    [Test]
    public async Task GetProduct_AsVipAndLoyaltyUser_ReturnsLoyaltyPrice()
    {
        // user-1 is seeded as both VIP (postgres) and loyalty (NATS KV)
        var token   = await GetTokenAsync("user-1");
        var product = await GetProductAsync("1", token);

        product.PriceVariant.Should().Be("loyalty-price");
        product.Price.Should().Be(79.99m);
    }

    // ── premium-product-page experiment ───────────────────────────────────

    [Test]
    public async Task GetProduct_AsFreeTierUser_ReturnsNoPremiumPage()
    {
        var token   = await GetTokenAsync("alice", plan: "free");
        var product = await GetProductAsync("42", token);

        product.PremiumPage.Should().BeFalse();
    }

    [Test]
    public async Task GetProduct_AsPremiumUser_ReturnsPremiumPage()
    {
        var token   = await GetTokenAsync("alice", plan: "premium");
        var product = await GetProductAsync("42", token);

        product.PremiumPage.Should().BeTrue();
    }

    // ── recommendation-algorithm experiment (OpenFeature) ─────────────────

    [Test]
    public async Task GetRecommendations_ReturnsValidAlgorithm()
    {
        var token    = await GetTokenAsync("alice");
        var response = await AuthGet("/recommendations", token);

        response.EnsureSuccessStatusCode();
        var recommendations = await response.Content.ReadFromJsonAsync<RecommendationsResponse>();
        recommendations!.Algorithm.Should().BeOneOf("collaborative", "content-based");
        recommendations.Items.Should().HaveCount(3);
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
        var token   = await GetTokenAsync(userId);
        var product = await GetProductAsync("1", token);

        product.PriceVariant.Should().Be("loyalty-price");

        // Clean up
        await fixture.AdminClient.DeleteAsync($"/loyalty/{userId}");
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    private async Task<string> GetTokenAsync(string userId, string plan = "free")
    {
        var response = await fixture.EcommerceClient
            .GetAsync($"/token?userId={Uri.EscapeDataString(userId)}&plan={Uri.EscapeDataString(plan)}");
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<TokenResponse>();
        return body!.Token;
    }

    private async Task<ProductResponse> GetProductAsync(string productId, string token)
    {
        var response = await AuthGet($"/products/{productId}", token);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<ProductResponse>())!;
    }

    private Task<HttpResponseMessage> AuthGet(string path, string token)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return fixture.EcommerceClient.SendAsync(request);
    }
}

using System.Net.Http.Headers;
using System.Net.Http.Json;
using AwesomeAssertions;
using Komento.Sample.Contracts;
using TUnit.Core;

namespace Komento.Sample.Tests;

[ClassDataSource<AppHostFixture>(Shared = SharedType.PerTestSession)]
public sealed class OpenFeatureContextTests(AppHostFixture fixture)
{
    [Test]
    [Arguments("premium", true)]
    [Arguments("free", false)]
    public async Task The_request_context_reaches_OpenFeature_evaluations(string plan, bool eligible)
    {
        var userId = $"context-user-{Guid.NewGuid():N}";
        var token  = await GetTokenAsync(userId, plan);

        using var request = new HttpRequestMessage(HttpMethod.Get, "/recommendations");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        (await fixture.EcommerceClient.SendAsync(request)).EnsureSuccessStatusCode();

        var exposure = (await WaitForExposuresAsync(userId, expected: 1)).Single();

        exposure.Experiment.Should().Be("recommendation-algorithm");
        exposure.SubjectId.Should().Be(userId);
        exposure.IsEligible.Should().Be(eligible);   // the premium-plan filter saw the user's plan
    }

    private async Task<string> GetTokenAsync(string userId, string plan)
    {
        var response = await fixture.EcommerceClient.GetAsync(
            $"/token?userId={Uri.EscapeDataString(userId)}&plan={Uri.EscapeDataString(plan)}");
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<TokenResponse>())!.Token;
    }

    private async Task<IReadOnlyList<ExposureResponse>> WaitForExposuresAsync(string subjectId, int expected)
    {
        using var cts   = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(500));
        IReadOnlyList<ExposureResponse> exposures = [];
        try
        {
            do
            {
                var response = await fixture.AdminClient.GetAsync($"/exposures?subjectId={Uri.EscapeDataString(subjectId)}", cts.Token);
                response.EnsureSuccessStatusCode();
                exposures = (await response.Content.ReadFromJsonAsync<ExposureResponse[]>(cts.Token))!;
                if (exposures.Count >= expected) return exposures;
            }
            while (await timer.WaitForNextTickAsync(cts.Token));
        }
        catch (OperationCanceledException) { }
        return exposures;
    }
}

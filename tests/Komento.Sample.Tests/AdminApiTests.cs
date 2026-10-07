using System.Net;
using System.Net.Http.Json;
using System.Text;
using AwesomeAssertions;
using TUnit.Core;

namespace Komento.Sample.Tests;

[ClassDataSource<AppHostFixture>(Shared = SharedType.PerTestSession)]
public sealed class AdminApiTests(AppHostFixture fixture)
{
    // The request body shown in the sample README (step 3), with the experiment id swapped per test.
    private const string ReadmeExperimentId = "premium-product-page";
    private const string ReadmeExperiment = """
        {
          "id": "premium-product-page",
          "subjectType": "user",
          "variants": [
            { "name": "on", "allocation": 1.0, "value": true }
          ],
          "globalFilters": [
            { "type": "trait-equals", "key": "plan", "value": "premium" }
          ],
          "overrides": []
        }
        """;

    [Test]
    public async Task Put_then_get_returns_the_experiment_config()
    {
        var id = $"admin-test-{Guid.NewGuid():N}";

        var put = await PutExperimentAsync(id, ReadmeExperiment.Replace(ReadmeExperimentId, id));
        put.StatusCode.Should().Be(HttpStatusCode.NoContent);

        var response = await fixture.AdminClient.GetAsync($"/experiments/{id}");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var config = await response.Content.ReadFromJsonAsync<ExperimentConfig>();

        config!.Id.Should().Be(id);
        config.SubjectType.Should().Be("user");
        config.Variants.Should().ContainSingle(v => v.Name == "on" && v.Allocation == 1.0);
        config.GlobalFilters.Should().ContainSingle()
            .Which.Should().BeEquivalentTo(new TraitEqualsFilter { Key = "plan", Value = "premium" });
    }

    [Test]
    public async Task Put_rejects_a_body_that_is_not_an_experiment_config()
    {
        var put = await PutExperimentAsync($"admin-test-{Guid.NewGuid():N}", "{ \"not\": \"an experiment\" }");

        put.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Test]
    public async Task Put_rejects_a_body_whose_id_differs_from_the_route()
    {
        var routeId = $"admin-test-{Guid.NewGuid():N}";

        var put = await PutExperimentAsync(routeId, ReadmeExperiment); // body id is premium-product-page

        put.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await fixture.AdminClient.GetAsync($"/experiments/{routeId}")).StatusCode
            .Should().Be(HttpStatusCode.NotFound);
    }

    [Test]
    public async Task Get_unknown_experiment_returns_not_found()
    {
        var response = await fixture.AdminClient.GetAsync($"/experiments/admin-test-{Guid.NewGuid():N}");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Test]
    public async Task Adding_a_vip_returns_no_content_and_lists_the_user()
    {
        var userId = $"vip-test-{Guid.NewGuid():N}";

        var post = await fixture.AdminClient.PostAsync($"/vip/{userId}", null);
        post.StatusCode.Should().Be(HttpStatusCode.NoContent);

        var vips = await fixture.AdminClient.GetFromJsonAsync<string[]>("/vip");
        vips.Should().Contain(userId);
    }

    private Task<HttpResponseMessage> PutExperimentAsync(string id, string json)
        => fixture.AdminClient.PutAsync(
            $"/experiments/{id}",
            new StringContent(json, Encoding.UTF8, "application/json"));
}

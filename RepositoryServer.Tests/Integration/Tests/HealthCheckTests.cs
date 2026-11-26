using System.Net;

namespace OpenShock.RepositoryServer.Tests.Integration.Tests;

/// <summary>
/// Covers the endpoint Kubernetes probes for startup and liveness.
/// </summary>
[NotInParallel("repo-server-integration")]
public class HealthCheckTests
{
    [ClassDataSource<WebApplicationFactory>(Shared = SharedType.PerTestSession)]
    public required WebApplicationFactory Factory { get; init; }

    [Test]
    public async Task Health_DatabaseReachable_ReturnsHealthyWithoutAuthentication()
    {
        using var client = Factory.CreateClient();

        var response = await client.GetAsync("/1/health");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(await response.Content.ReadAsStringAsync()).IsEqualTo("Healthy");
    }
}

using System.Net;
using System.Text.Json;

namespace OpenShock.RepositoryServer.Tests.Integration.Tests;

/// <summary>
/// Covers the single OpenAPI document that every API version is described in.
/// </summary>
[NotInParallel("repo-server-integration")]
public class OpenApiDocumentTests
{
    [ClassDataSource<WebApplicationFactory>(Shared = SharedType.PerTestSession)]
    public required WebApplicationFactory Factory { get; init; }

    [Test]
    public async Task GetOpenApiDocument_ServesSingleDocumentWithEveryVersion()
    {
        using var client = Factory.CreateClient();

        var response = await client.GetAsync("/openapi.json");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(response.Content.Headers.ContentType?.MediaType).IsEqualTo("application/json");

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var paths = doc.RootElement.GetProperty("paths").EnumerateObject().Select(p => p.Name).ToArray();

        await Assert.That(paths).Contains("/1");
        await Assert.That(paths).Contains("/2/firmware/manifest");

        // CI/CD ingestion is not part of the public surface
        await Assert.That(paths.Any(p => p.Contains("/cicd") || p.Contains("/firmware/releases"))).IsFalse();
    }

    [Test]
    public async Task GetOpenApiDocument_DescribesWhatTheBuiltInSupportLeavesOut()
    {
        using var client = Factory.CreateClient();

        using var doc = JsonDocument.Parse(await client.GetStringAsync("/openapi.json"));
        var root = doc.RootElement;

        // Every tag is described by the <summary> of its controller
        var undescribedTags = root.GetProperty("tags").EnumerateArray()
            .Where(tag => !tag.TryGetProperty("description", out var description) || string.IsNullOrWhiteSpace(description.GetString()))
            .Select(tag => tag.GetProperty("name").GetString())
            .ToArray();
        await Assert.That(undescribedTags).IsEmpty();

        // Every operation has an id, so generated clients get stable method names
        var operationsWithoutId = root.GetProperty("paths").EnumerateObject()
            .SelectMany(path => path.Value.EnumerateObject().Select(operation => (Path: path.Name, Operation: operation.Value)))
            .Where(x => !x.Operation.TryGetProperty("operationId", out _))
            .Select(x => x.Path)
            .ToArray();
        await Assert.That(operationsWithoutId).IsEmpty();

        // A response number is a number, not the "integer or numeric string" union the exporter derives from the JSON options
        var fileSizeType = root.GetProperty("components").GetProperty("schemas").GetProperty("FirmwareArtifactDto")
            .GetProperty("properties").GetProperty("fileSize").GetProperty("type");
        await Assert.That(fileSizeType.ValueKind == JsonValueKind.String ? fileSizeType.GetString() : fileSizeType.ToString()).IsEqualTo("integer");
    }
}

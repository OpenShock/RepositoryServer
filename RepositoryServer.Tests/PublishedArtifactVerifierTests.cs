using Microsoft.Extensions.Logging.Abstractions;
using OpenShock.RepositoryServer.Config;
using OpenShock.RepositoryServer.Enums;
using OpenShock.RepositoryServer.Services;
using System.Net;

namespace OpenShock.RepositoryServer.Tests;

/// <summary>
/// Covers the publish-time reachability probe against a stubbed transport.
/// </summary>
/// <remarks>
/// No database and no container: what is under test is how the verifier reads HTTP responses, so
/// the responses are supplied directly. That also keeps the probe's own behaviour tested in an
/// environment where the integration suite has it switched off, which it must be — the suite's
/// CdnBaseUrl is a hostname invented not to resolve.
/// </remarks>
public class PublishedArtifactVerifierTests
{
    private static readonly Guid Board = Guid.Parse("11111111-1111-1111-1111-111111111111");

    private static readonly (Guid, FirmwareArtifactType)[] OneArtifact =
        [(Board, FirmwareArtifactType.App)];

    /// <summary>Answers every request the same way, and counts them.</summary>
    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;
        public int Calls { get; private set; }
        public List<HttpMethod> Methods { get; } = [];

        public StubHandler(HttpStatusCode status)
            : this(_ => new HttpResponseMessage(status)) { }

        public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) => _respond = respond;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            Methods.Add(request.Method);
            return Task.FromResult(_respond(request));
        }
    }

    private static PublishedArtifactVerifier Build(StubHandler handler, bool verify = true) =>
        new(new HttpClient(handler),
            new ApiConfig
            {
                Db = new DbConfig { Conn = "Host=localhost;Database=x;Username=x;Password=x" },
                Modules = new ModulesConfig
                {
                    Id = "test", Name = "test", Author = "test",
                    CdnBaseUrl = "https://cdn.example/repo",
                    Storage = new StorageConfig { Type = StorageType.Local }
                },
                Firmware = new FirmwareConfig
                {
                    CdnBaseUrl = "https://cdn.example/firmware",
                    Storage = new StorageConfig { Type = StorageType.Local },
                    VerifyPublishedArtifacts = verify
                },
                CiCd = new CiCdConfig { Audience = "test" }
            },
            NullLogger<PublishedArtifactVerifier>.Instance);

    [Test]
    public async Task Reachable_ReportsNothing()
    {
        var handler = new StubHandler(HttpStatusCode.OK);

        var unreachable = await Build(handler).FindUnreachableAsync("1.0.0", OneArtifact);

        await Assert.That(unreachable).IsEmpty();
        // HEAD, not GET: these are megabytes and the body is not wanted.
        await Assert.That(handler.Methods).Contains(HttpMethod.Head);
    }

    /// <summary>
    /// The case this exists for: the object was written to storage, and the hostname the server
    /// advertises does not serve it.
    /// </summary>
    [Test]
    public async Task NotFound_IsReported()
    {
        var unreachable = await Build(new StubHandler(HttpStatusCode.NotFound))
            .FindUnreachableAsync("1.0.0", OneArtifact);

        await Assert.That(unreachable).Count().IsEqualTo(1);
        await Assert.That(unreachable[0].Status).IsEqualTo(HttpStatusCode.NotFound);
        await Assert.That(unreachable[0].Url).Contains("1.0.0");
    }

    /// <summary>
    /// A store that does not implement HEAD says so with 405. The object is evidently there, so
    /// refusing the publish over an unsupported method would be a false failure.
    /// </summary>
    [Test]
    public async Task MethodNotAllowed_CountsAsPresent()
    {
        var unreachable = await Build(new StubHandler(HttpStatusCode.MethodNotAllowed))
            .FindUnreachableAsync("1.0.0", OneArtifact);

        await Assert.That(unreachable).IsEmpty();
    }

    [Test]
    public async Task TransportFailure_IsReported()
    {
        var handler = new StubHandler(_ => throw new HttpRequestException("no such host"));

        var unreachable = await Build(handler).FindUnreachableAsync("1.0.0", OneArtifact);

        await Assert.That(unreachable).Count().IsEqualTo(1);
        await Assert.That(unreachable[0].Status).IsNull();
    }

    /// <summary>
    /// A miss is retried before it counts, so edge propagation does not fail a good publish. An
    /// artifact that appears on the second attempt is reachable.
    /// </summary>
    [Test]
    public async Task Miss_IsRetriedBeforeItCounts()
    {
        var attempt = 0;
        var handler = new StubHandler(_ =>
            new HttpResponseMessage(++attempt == 1 ? HttpStatusCode.NotFound : HttpStatusCode.OK));

        var unreachable = await Build(handler).FindUnreachableAsync("1.0.0", OneArtifact);

        await Assert.That(unreachable).IsEmpty();
        await Assert.That(handler.Calls).IsEqualTo(2);
    }

    /// <summary>
    /// Switched off, it makes no requests at all — for deployments that genuinely cannot reach
    /// their own CDN hostname, and for the integration suite.
    /// </summary>
    [Test]
    public async Task Disabled_MakesNoRequests()
    {
        var handler = new StubHandler(HttpStatusCode.NotFound);

        var unreachable = await Build(handler, verify: false).FindUnreachableAsync("1.0.0", OneArtifact);

        await Assert.That(unreachable).IsEmpty();
        await Assert.That(handler.Calls).IsEqualTo(0);
    }

    /// <summary>
    /// One request per artifact, not per board: a release is up to five artifacts across a dozen
    /// boards and every one of them has to be accounted for.
    /// </summary>
    [Test]
    public async Task ProbesEveryArtifact()
    {
        var handler = new StubHandler(HttpStatusCode.OK);
        (Guid, FirmwareArtifactType)[] artifacts =
        [
            (Board, FirmwareArtifactType.App),
            (Board, FirmwareArtifactType.StaticFs),
            (Board, FirmwareArtifactType.Merged)
        ];

        await Build(handler).FindUnreachableAsync("1.0.0", artifacts);

        await Assert.That(handler.Calls).IsEqualTo(3);
    }
}

using System.Reflection;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace OpenShock.RepositoryServer.OpenApi;

/// <summary>
/// The single OpenAPI document. Every API version is in it: routes carry their version as a plain leading segment
/// (<c>/1</c>, <c>/2/firmware/...</c>), so the paths alone keep them apart.
/// </summary>
public static class OpenApiSetup
{
    /// <summary>
    /// Name of the document. It must be the framework's default ("v1"): <see cref="DocumentPath"/> has no
    /// <c>{documentName}</c> segment, so MapOpenApi serves the default document there. It never appears in a URL.
    /// </summary>
    public const string DocumentName = "v1";

    /// <summary>
    /// Path the OpenAPI document is served at.
    /// </summary>
    public const string DocumentPath = "/openapi.json";

    /// <summary>
    /// Configures the document. Registered with <c>services.AddOpenApi(DocumentName, Configure)</c> from Program.cs: the XML
    /// documentation comments are applied by a source generator that intercepts that call.
    /// </summary>
    public static void Configure(OpenApiOptions options)
    {
        // Servers are left to the framework, which lists the origin the document was requested from. The server is self-hosted,
        // so a fixed list would point a staging instance's "try it" at production.
        options.AddDocumentTransformer((document, _, _) =>
        {
            document.Info.Title = "OpenShock.RepositoryServer";
            document.Info.Version = "1.0";
            document.Info.Description = Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyDescriptionAttribute>()?.Description;

            return Task.CompletedTask;
        });

        options.OpenApiVersion = OpenApiSpecVersion.OpenApi3_2;

        options.AddOperationTransformer<OperationTransformer>();
        options.AddSchemaTransformer<SchemaTransformer>();

        // Runs after the built-in XML comment transformers (registered before this method), filling what they leave out
        options.AddDocumentTransformer<ControllerTagDescriptionTransformer>();
    }
}

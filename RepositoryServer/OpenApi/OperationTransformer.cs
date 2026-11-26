using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace OpenShock.RepositoryServer.OpenApi;

public sealed class OperationTransformer : IOpenApiOperationTransformer
{
    public Task TransformAsync(OpenApiOperation operation, OpenApiOperationTransformerContext context, CancellationToken cancellationToken)
    {
        var actionDescriptor = context.Description.ActionDescriptor;

        // Without one, client generators name methods after the path, and two versions of a path would collide
        operation.OperationId =
            $"{actionDescriptor.RouteValues["controller"]}_{actionDescriptor.AttributeRouteInfo?.Name ?? actionDescriptor.RouteValues["action"]}";

        if (actionDescriptor is not ControllerActionDescriptor { MethodInfo: var methodInfo }) return Task.CompletedTask;

        if (methodInfo.IsDefined(typeof(ObsoleteAttribute), true) || methodInfo.DeclaringType?.IsDefined(typeof(ObsoleteAttribute), true) == true)
        {
            operation.Deprecated = true;
        }

        // A default value on a query parameter is exported as a single-value enum (e.g. enum: [""]), which makes clients reject any other value
        foreach (var parameter in operation.Parameters ?? [])
        {
            if (parameter.Schema is OpenApiSchema { Enum.Count: 1, Default: not null } schema && JsonNode.DeepEquals(schema.Enum[0], schema.Default))
            {
                schema.Enum = null;
            }
        }

        return Task.CompletedTask;
    }
}

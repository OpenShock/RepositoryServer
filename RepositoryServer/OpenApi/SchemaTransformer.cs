using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace OpenShock.RepositoryServer.OpenApi;

public sealed class SchemaTransformer : IOpenApiSchemaTransformer
{
    public Task TransformAsync(OpenApiSchema schema, OpenApiSchemaTransformerContext context, CancellationToken cancellationToken)
    {
        NormalizeNumbers(schema);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Two things the exporter leaves awkward for a client:
    /// <list type="bullet">
    /// <item>JsonNumberHandling.AllowReadingFromString makes every number an "integer or numeric string" union, which would
    /// have clients type every size as <c>number | string</c>. A response always carries a number, so the string half goes.</item>
    /// <item>Unsigned formats have no OpenAPI equivalent that generators recognise, and an unrecognised format falls back to
    /// a plain number, silently losing precision past 2^53. Each becomes the narrowest signed format that covers it, with the
    /// real range as bounds. uint32 keeps no format at all: int64 would push clients to bigint for a value a double holds
    /// exactly, while int32 would misstate the upper half of its range.</item>
    /// </list>
    /// </summary>
    private static void NormalizeNumbers(OpenApiSchema schema)
    {
        if (schema.Type is not { } type) return;

        if ((type & (JsonSchemaType.Integer | JsonSchemaType.Number)) != 0 && type.HasFlag(JsonSchemaType.String))
        {
            schema.Type = type & ~JsonSchemaType.String;
            if (schema.Pattern?.StartsWith("^-?(?:0|[1-9]", StringComparison.Ordinal) == true) schema.Pattern = null;
        }

        (string? Format, string Maximum)? unsigned = schema.Format switch
        {
            "uint8" => ("int32", "255"),
            "uint16" => ("int32", "65535"),
            "uint32" => (null, "4294967295"),
            "uint64" => ("int64", "18446744073709551615"),
            _ => null
        };
        if (unsigned is not { } signed) return;

        schema.Format = signed.Format;
        // A [Range] attribute is narrower than the type's own range, so it wins.
        schema.Minimum ??= "0";
        schema.Maximum ??= signed.Maximum;
    }
}

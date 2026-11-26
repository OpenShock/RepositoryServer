namespace OpenShock.RepositoryServer.ApiVersioning;

/// <summary>
/// The API version a controller is served under, filled into the <c>{version:apiVersion}</c> placeholder of its route:
/// a controller routed at <c>/{version:apiVersion}/firmware/boards</c> with <c>[ApiVersion(2)]</c> serves <c>/2/firmware/boards</c>.
/// </summary>
/// <remarks>
/// On a controller it applies to all of that controller's actions; an attribute on an action takes precedence. A controller without
/// a version is not versioned, and its route may not contain the placeholder. See <see cref="ApiVersionRouteConvention"/>.
/// </remarks>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, Inherited = true)]
public sealed class ApiVersionAttribute : Attribute
{
    public ApiVersionAttribute(uint version)
    {
        ArgumentOutOfRangeException.ThrowIfZero(version);

        Version = version;
    }

    public uint Version { get; }
}

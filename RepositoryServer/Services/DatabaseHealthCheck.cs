using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using OpenShock.RepositoryServer.RepoServerDb;

namespace OpenShock.RepositoryServer.Services;

/// <summary>
/// Reports unhealthy when Postgres cannot be reached. Every request the server answers goes through
/// the database, so an instance without it has nothing useful to serve.
/// </summary>
public sealed class DatabaseHealthCheck : IHealthCheck
{
    private readonly IDbContextFactory<RepoServerContext> _dbFactory;

    public DatabaseHealthCheck(IDbContextFactory<RepoServerContext> dbFactory)
    {
        _dbFactory = dbFactory;
    }

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        return await db.Database.CanConnectAsync(cancellationToken)
            ? HealthCheckResult.Healthy()
            : HealthCheckResult.Unhealthy("Database is unreachable");
    }
}

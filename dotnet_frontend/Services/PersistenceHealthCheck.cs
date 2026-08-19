using DoedRegulatoryComments.Web.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace DoedRegulatoryComments.Web.Services;

public sealed class PersistenceHealthCheck : IHealthCheck
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IConfiguration _configuration;

    public PersistenceHealthCheck(
        IServiceScopeFactory scopeFactory,
        IConfiguration configuration)
    {
        _scopeFactory = scopeFactory;
        _configuration = configuration;
    }

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var provider = _configuration["Persistence:Provider"]?.Trim() ?? "Sqlite";
            if (provider.Equals("Cosmos", StringComparison.OrdinalIgnoreCase))
            {
                var containers = scope.ServiceProvider.GetRequiredService<CosmosContainerSet>();
                await containers.Runs.ReadContainerAsync(cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
            }
            else
            {
                var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<AnalysisDbContext>>();
                await using var database = await factory.CreateDbContextAsync(cancellationToken)
                    .ConfigureAwait(false);
                if (!await database.Database.CanConnectAsync(cancellationToken).ConfigureAwait(false))
                    return HealthCheckResult.Unhealthy("Analysis database rejected the connectivity check.");
            }
            return HealthCheckResult.Healthy("Analysis persistence is reachable.");
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy("Analysis persistence is unavailable.", ex);
        }
    }
}
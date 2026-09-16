using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Xunit;
using DoedRegulatoryComments.Web.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace DoedRegulatoryComments.Web.Tests;

public sealed class HealthEndpointIntegrationTests : IClassFixture<HealthWebApplicationFactory>
{
    private readonly HealthWebApplicationFactory _factory;

    public HealthEndpointIntegrationTests(HealthWebApplicationFactory factory) => _factory = factory;

    [Theory]
    [InlineData("/health/live")]
    [InlineData("/health/ready")]
    public async Task HealthEndpoint_ReturnsHealthy(string path)
    {
        using var client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost"),
        });

        using var response = await client.GetAsync(path);
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Healthy", body);
    }
}

public sealed class HealthWebApplicationFactory : WebApplicationFactory<Program>
{
    private readonly string _databasePath = Path.Combine(
        Path.GetTempPath(),
        $"doed-health-{Guid.NewGuid():N}.db");

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<IDbContextFactory<AnalysisDbContext>>();
            services.RemoveAll<DbContextOptions<AnalysisDbContext>>();
            services.RemoveAll<IDbContextOptionsConfiguration<AnalysisDbContext>>();
            services.AddDbContextFactory<AnalysisDbContext>(options => options.UseSqlite($"Data Source={_databasePath};Pooling=False"));
        });
        builder.ConfigureAppConfiguration((_, configuration) =>
        {
            configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["APPLICATIONINSIGHTS_CONNECTION_STRING"] = string.Empty,
                ["Persistence:Provider"] = "Sqlite",
                ["ConnectionStrings:AnalysisDb"] = $"Data Source={_databasePath};Pooling=False",
                ["Persistence:Payloads:CreateIfNotExists"] = "false",
                ["Attachments:OcrEndpoint"] = string.Empty,
            });
        });
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing && File.Exists(_databasePath)) File.Delete(_databasePath);
    }
}
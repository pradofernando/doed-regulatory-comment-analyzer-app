using Azure.Core;
using Azure.Identity;
using Azure.Monitor.OpenTelemetry.AspNetCore;
using System.Collections.ObjectModel;
using DoedRegulatoryComments.Web.Components;
using DoedRegulatoryComments.Web.Data;
using DoedRegulatoryComments.Web.Services;
using Microsoft.Azure.Cosmos;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.EntityFrameworkCore;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSingleton<TokenCredential>(_ => builder.Environment.IsDevelopment()
    ? new DefaultAzureCredential()
    : new ManagedIdentityCredential(ManagedIdentityId.SystemAssigned));

// Add services to the container.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

builder.Services.AddOptions<FoundryCostOptions>()
    .Bind(builder.Configuration.GetSection(FoundryCostOptions.SectionName))
    .Validate(options => options.InputUsdPerMillionTokens >= 0
        && options.OutputUsdPerMillionTokens >= 0,
        "Telemetry token prices cannot be negative.")
    .ValidateOnStart();
builder.Services.AddSingleton<OperationalTelemetry>();

if (!string.IsNullOrWhiteSpace(builder.Configuration["APPLICATIONINSIGHTS_CONNECTION_STRING"]))
{
    builder.Services.AddOpenTelemetry().UseAzureMonitor();
    builder.Services.ConfigureOpenTelemetryTracerProvider((_, tracing) =>
        tracing.AddSource(OperationalTelemetry.InstrumentationName));
    builder.Services.ConfigureOpenTelemetryMeterProvider((_, metrics) =>
        metrics.AddMeter(OperationalTelemetry.InstrumentationName));
}

builder.Services.AddHealthChecks()
    .AddCheck<PersistenceHealthCheck>("persistence", tags: ["ready"]);

builder.Services.AddOptions<AttachmentProcessingOptions>()
    .Bind(builder.Configuration.GetSection(AttachmentProcessingOptions.SectionName))
    .Validate(options => options.AllowedHosts.Length > 0, "At least one attachment host must be allowed.")
    .Validate(options => options.MaxDownloadBytes is >= 1024 and <= 104857600,
        "Attachments:MaxDownloadBytes must be between 1 KB and 100 MB.")
    .Validate(options => options.MaxRedirects is >= 0 and <= 10,
        "Attachments:MaxRedirects must be between 0 and 10.")
    .Validate(options => options.MaxArchiveEntries is >= 1 and <= 10000,
        "Attachments:MaxArchiveEntries must be between 1 and 10,000.")
    .Validate(options => options.MaxArchiveUncompressedBytes is >= 1048576 and <= 524288000,
        "Attachments:MaxArchiveUncompressedBytes must be between 1 MB and 500 MB.")
    .Validate(options => options.MaxExtractedTextCharacters is >= 1000 and <= 5000000,
        "Attachments:MaxExtractedTextCharacters must be between 1,000 and 5,000,000.")
    .Validate(options => options.MaxPdfPages is >= 1 and <= 2000,
        "Attachments:MaxPdfPages must be between 1 and 2,000.")
    .Validate(options => options.MaxOcrPages is >= 1 and <= 2000,
        "Attachments:MaxOcrPages must be between 1 and 2,000.")
    .Validate(options => options.MinPdfTextCharactersPerPage is >= 1 and <= 1000,
        "Attachments:MinPdfTextCharactersPerPage must be between 1 and 1,000.")
    .Validate(options => string.IsNullOrWhiteSpace(options.OcrEndpoint)
        || (Uri.TryCreate(options.OcrEndpoint, UriKind.Absolute, out var endpoint)
            && endpoint.Scheme == Uri.UriSchemeHttps),
        "Attachments:OcrEndpoint must be an absolute HTTPS URL when configured.")
    .ValidateOnStart();
builder.Services.AddOptions<AnalysisPayloadOptions>()
    .Bind(builder.Configuration.GetSection(AnalysisPayloadOptions.SectionName))
    .Validate(options => options.OffloadThresholdBytes is >= 65536 and <= 1572864,
        "Persistence:Payloads:OffloadThresholdBytes must be between 64 KB and 1.5 MB.")
    .Validate(options => string.IsNullOrWhiteSpace(options.BlobContainerUri)
        || (Uri.TryCreate(options.BlobContainerUri, UriKind.Absolute, out var uri)
            && uri.Scheme == Uri.UriSchemeHttps),
        "Persistence:Payloads:BlobContainerUri must be an absolute HTTPS URL when configured.")
    .ValidateOnStart();
builder.Services.AddSingleton<IAnalysisPayloadStore, BlobAnalysisPayloadStore>();

// API settings + typed client for the regulatory comments backend.
builder.Services.AddSingleton<ApiSettingsStore>();
builder.Services.AddHttpClient<RegulationsGovClient>(client =>
{
    client.Timeout = TimeSpan.FromSeconds(60);
    client.DefaultRequestHeaders.UserAgent.ParseAdd("DoedRegulatoryComments.Web/1.0");
})
.ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
{
    AllowAutoRedirect = false,
    PooledConnectionLifetime = TimeSpan.FromMinutes(10),
});

// AI agent analysis (mirrors the Python function app workflow).
// Scoped because AttachmentExtractor depends on the typed-client RegulationsGovClient,
// which AddHttpClient registers as Transient.
builder.Services.AddScoped<AttachmentExtractor>();
builder.Services.AddSingleton<IDocumentOcrService, AzureDocumentOcrService>();
builder.Services.AddHttpClient("foundry", c =>
{
    // Foundry Responses API calls can run several minutes when batches are large or rate-limit retries kick in.
    c.Timeout = TimeSpan.FromMinutes(10);
});
builder.Services.AddScoped<FoundryAnalysisService>();
builder.Services.AddScoped<IAnalysisRunner>(sp => sp.GetRequiredService<FoundryAnalysisService>());
builder.Services.AddScoped<AnalysisStore>();

// Per-circuit cache of the last Comments fetch (so opening a comment + going back doesn't re-fetch).
builder.Services.AddScoped<CommentsBrowseState>();

// Runs analysis jobs detached from the Blazor circuit so navigation/reconnect doesn't kill them.
builder.Services.AddSingleton<AnalysisJobManager>();

// Persistence is selected with Persistence:Provider: Sqlite, AzureSql, or Cosmos.
var persistenceProvider = builder.Configuration["Persistence:Provider"]?.Trim() ?? "Sqlite";
var usesRelationalPersistence = false;

if (persistenceProvider.Equals("Sqlite", StringComparison.OrdinalIgnoreCase))
{
    var connectionString = builder.Configuration.GetConnectionString("AnalysisDb")
        ?? $"Data Source={Path.Combine(builder.Environment.ContentRootPath, "App_Data", "analysis.db")}";
    Directory.CreateDirectory(Path.Combine(builder.Environment.ContentRootPath, "App_Data"));
    builder.Services.AddDbContextFactory<AnalysisDbContext>(options => options.UseSqlite(connectionString));
    builder.Services.AddScoped<AnalysisRepository>();
    builder.Services.AddScoped<IAnalysisRepository>(sp => sp.GetRequiredService<AnalysisRepository>());
    usesRelationalPersistence = true;
}
else if (persistenceProvider.Equals("AzureSql", StringComparison.OrdinalIgnoreCase)
         || persistenceProvider.Equals("SqlServer", StringComparison.OrdinalIgnoreCase))
{
    var connectionString = builder.Configuration.GetConnectionString("AnalysisDb")
        ?? throw new InvalidOperationException(
            "ConnectionStrings:AnalysisDb is required when Persistence:Provider is AzureSql.");
    builder.Services.AddDbContextFactory<AnalysisDbContext>(options =>
        options.UseSqlServer(connectionString, sql => sql.EnableRetryOnFailure()));
    builder.Services.AddScoped<AnalysisRepository>();
    builder.Services.AddScoped<IAnalysisRepository>(sp => sp.GetRequiredService<AnalysisRepository>());
    usesRelationalPersistence = true;
}
else if (persistenceProvider.Equals("Cosmos", StringComparison.OrdinalIgnoreCase))
{
    var cosmosOptions = builder.Configuration
        .GetSection(CosmosPersistenceOptions.SectionName)
        .Get<CosmosPersistenceOptions>() ?? new CosmosPersistenceOptions();
    cosmosOptions.Validate();

    builder.Services.AddSingleton(cosmosOptions);
    builder.Services.AddSingleton(sp =>
    {
        var clientOptions = new CosmosClientOptions
        {
            ApplicationName = "DoedRegulatoryComments.Web",
            ConnectionMode = ConnectionMode.Direct,
        };

        return string.IsNullOrWhiteSpace(cosmosOptions.ConnectionString)
            ? new CosmosClient(cosmosOptions.Endpoint, sp.GetRequiredService<TokenCredential>(), clientOptions)
            : new CosmosClient(cosmosOptions.ConnectionString, clientOptions);
    });
    builder.Services.AddSingleton(sp =>
    {
        var client = sp.GetRequiredService<CosmosClient>();
        var runs = client.GetContainer(cosmosOptions.DatabaseName, cosmosOptions.ContainerName);
        var hasDedicatedSummaries = !string.IsNullOrWhiteSpace(cosmosOptions.SummaryContainerName)
            && !cosmosOptions.SummaryContainerName.Equals(
                cosmosOptions.ContainerName,
                StringComparison.OrdinalIgnoreCase);
        var summaries = hasDedicatedSummaries
            ? client.GetContainer(cosmosOptions.DatabaseName, cosmosOptions.SummaryContainerName)
            : runs;
        return new CosmosContainerSet(runs, summaries, hasDedicatedSummaries);
    });
    builder.Services.AddScoped<IAnalysisRepository, CosmosAnalysisRepository>();
}
else
{
    throw new InvalidOperationException(
        $"Unsupported Persistence:Provider '{persistenceProvider}'. Use Sqlite, AzureSql, or Cosmos.");
}

var app = builder.Build();

var payloadOptions = app.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<AnalysisPayloadOptions>>().Value;
if (payloadOptions.CreateIfNotExists)
{
    await app.Services.GetRequiredService<IAnalysisPayloadStore>().EnsureCreatedAsync();
}

if (usesRelationalPersistence)
{
    using var scope = app.Services.CreateScope();
    var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<AnalysisDbContext>>();
    await AnalysisDatabaseInitializer.InitializeAsync(factory);
}
else
{
    var cosmosOptions = app.Services.GetRequiredService<CosmosPersistenceOptions>();
    if (cosmosOptions.CreateIfNotExists)
    {
        var client = app.Services.GetRequiredService<CosmosClient>();
        var database = await client.CreateDatabaseIfNotExistsAsync(cosmosOptions.DatabaseName);
        var runContainer = new ContainerProperties(cosmosOptions.ContainerName, "/id");
        runContainer.IndexingPolicy.ExcludedPaths.Add(new ExcludedPath
        {
            Path = "/categorizations/[]/rawResponse/?",
        });
        runContainer.IndexingPolicy.ExcludedPaths.Add(new ExcludedPath
        {
            Path = "/categorizations/[]/parsedJson/?",
        });
        runContainer.IndexingPolicy.ExcludedPaths.Add(new ExcludedPath
        {
            Path = "/followUpHistory/[]/text/?",
        });
        await database.Database.CreateContainerIfNotExistsAsync(runContainer);

        if (!string.IsNullOrWhiteSpace(cosmosOptions.SummaryContainerName)
            && !cosmosOptions.SummaryContainerName.Equals(
                cosmosOptions.ContainerName,
                StringComparison.OrdinalIgnoreCase))
        {
            var summaryContainer = new ContainerProperties(
                cosmosOptions.SummaryContainerName,
                "/documentIdNormalized");
            summaryContainer.IndexingPolicy.CompositeIndexes.Add(new Collection<CompositePath>
            {
                new() { Path = "/type", Order = CompositePathSortOrder.Ascending },
                new() { Path = "/startedAt", Order = CompositePathSortOrder.Descending },
            });
            summaryContainer.IndexingPolicy.CompositeIndexes.Add(new Collection<CompositePath>
            {
                new() { Path = "/type", Order = CompositePathSortOrder.Ascending },
                new() { Path = "/succeeded", Order = CompositePathSortOrder.Ascending },
                new() { Path = "/startedAt", Order = CompositePathSortOrder.Descending },
            });
            await database.Database.CreateContainerIfNotExistsAsync(summaryContainer);
        }
    }
}

app.Logger.LogInformation("Analysis persistence provider: {Provider}", persistenceProvider);

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}

app.UseHttpsRedirection();


app.UseAntiforgery();

app.MapHealthChecks("/health/live", new HealthCheckOptions
{
    Predicate = _ => false,
});
app.MapHealthChecks("/health/ready", new HealthCheckOptions
{
    Predicate = registration => registration.Tags.Contains("ready"),
});

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();

public partial class Program;

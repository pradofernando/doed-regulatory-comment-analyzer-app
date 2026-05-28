using DoedRegulatoryComments.Web.Components;
using DoedRegulatoryComments.Web.Data;
using DoedRegulatoryComments.Web.Services;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

// API settings + typed client for the regulatory comments backend.
builder.Services.AddSingleton<ApiSettingsStore>();
builder.Services.AddHttpClient<RegulationsGovClient>(client =>
{
    client.Timeout = TimeSpan.FromSeconds(60);
    client.DefaultRequestHeaders.UserAgent.ParseAdd("DoedRegulatoryComments.Web/1.0");
});

// AI agent analysis (mirrors the Python function app workflow).
// Scoped because AttachmentExtractor depends on the typed-client RegulationsGovClient,
// which AddHttpClient registers as Transient.
builder.Services.AddScoped<AttachmentExtractor>();
builder.Services.AddHttpClient("foundry", c =>
{
    // Foundry Responses API calls can run several minutes when batches are large or rate-limit retries kick in.
    c.Timeout = TimeSpan.FromMinutes(10);
});
builder.Services.AddScoped<FoundryAnalysisService>();
builder.Services.AddScoped<AnalysisStore>();

// Persistence: SQLite analysis-run history.
// Connection string defaults to a file under App_Data/; overridable via
// ConnectionStrings:AnalysisDb in appsettings or environment.
var connString = builder.Configuration.GetConnectionString("AnalysisDb")
    ?? $"Data Source={Path.Combine(builder.Environment.ContentRootPath, "App_Data", "analysis.db")}";
Directory.CreateDirectory(Path.Combine(builder.Environment.ContentRootPath, "App_Data"));
builder.Services.AddDbContextFactory<AnalysisDbContext>(opt => opt.UseSqlite(connString));
builder.Services.AddScoped<AnalysisRepository>();

var app = builder.Build();

// Ensure the SQLite schema exists. For SQLite we use EnsureCreated rather than migrations —
// the schema is small and forward-only changes can be handled by manual migration if/when needed.
using (var scope = app.Services.CreateScope())
{
    var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<AnalysisDbContext>>();
    await using var db = await factory.CreateDbContextAsync();
    await db.Database.EnsureCreatedAsync();
}

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}

app.UseHttpsRedirection();


app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();

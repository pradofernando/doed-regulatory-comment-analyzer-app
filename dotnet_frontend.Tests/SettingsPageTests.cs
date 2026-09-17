using DoedRegulatoryComments.Web.Components.Layout;
using DoedRegulatoryComments.Web.Components.Pages;
using DoedRegulatoryComments.Web.Services;
using HtmlAgilityPack;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace DoedRegulatoryComments.Web.Tests;

public sealed class SettingsPageTests
{
    [Fact]
    public async Task FunctionManagedSettings_ExposeOnlyRunPreferences()
    {
        var document = await RenderSettings(functionBackend: true);
        var text = document.DocumentNode.InnerText;
        Assert.Contains("Run defaults", text);
        Assert.Contains("Managed by Function App", text);
        Assert.Contains("Save preferences", text);
        Assert.Contains("Reset preferences", text);
        Assert.NotNull(document.DocumentNode.SelectSingleNode("//input[@id='defaultDocumentId']"));
        Assert.NotNull(document.DocumentNode.SelectSingleNode("//input[@id='batchSize']"));
        Assert.NotNull(document.DocumentNode.SelectSingleNode("//input[@id='runValidation']"));
        Assert.Equal(3, document.DocumentNode.SelectNodes("//form//input[not(@type='hidden')]").Count);
        Assert.Null(document.DocumentNode.SelectSingleNode("//input[@type='password']"));
        Assert.DoesNotContain("synthetic-settings-key", text);
        Assert.DoesNotContain("synthetic-settings-key", document.DocumentNode.OuterHtml);
        Assert.DoesNotContain("foundry.example.test", document.DocumentNode.OuterHtml);
    }

    [Fact]
    public async Task StandaloneSettings_PreserveDirectConnectionControls()
    {
        var document = await RenderSettings(functionBackend: false);
        Assert.Contains("Comments backend", document.DocumentNode.InnerText);
        Assert.Contains("AI agents", document.DocumentNode.InnerText);
        Assert.NotNull(document.DocumentNode.SelectSingleNode("//input[@type='password']"));
        Assert.DoesNotContain("Save preferences", document.DocumentNode.InnerText);
    }

    [Theory]
    [InlineData(true, "Azure Function App")]
    [InlineData(false, "Override on the Settings page")]
    public async Task Sidebar_UsesBackendStatusAndPreservesWorkspaceLinks(bool functionBackend, string backendText)
    {
        var document = await Render<NavMenu>(functionBackend);
        var footer = document.DocumentNode.SelectSingleNode("//div[contains(@class,'sidebar-footer__hint')]");
        Assert.NotNull(footer);
        Assert.Contains(backendText, footer.InnerText);
        var links = document.DocumentNode.SelectNodes("//nav//a").Select(node => node.GetAttributeValue("href", "")).ToList();
        Assert.Contains("watchlists", links);
        Assert.Contains("library", links);
        Assert.Equal(links.IndexOf("compare") + 1, links.IndexOf("notifications"));
    }

    private static async Task<HtmlDocument> Render<T>(bool functionBackend) where T : IComponent
    {
        var environment = new TestEnvironment();
        var configuration = Configuration();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddSingleton<IHostEnvironment>(environment);
        services.AddSingleton<IWebHostEnvironment>(environment);
        services.AddRazorComponents();
        services.AddSingleton<NavigationManager>(new TestNavigationManager());
        services.AddSingleton(new ApiSettingsStore(configuration, environment));
        services.AddSingleton<IOptions<FunctionAnalysisOptions>>(Options.Create(new FunctionAnalysisOptions { Enabled = functionBackend }));
        using var provider = services.BuildServiceProvider();
        await using var renderer = new HtmlRenderer(provider, provider.GetRequiredService<ILoggerFactory>());
        var html = await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var output = await renderer.RenderComponentAsync<T>(ParameterView.Empty);
            return output.ToHtmlString();
        });
        var document = new HtmlDocument();
        document.LoadHtml(html);
        return document;
    }

    private static async Task<HtmlDocument> RenderSettings(bool functionBackend)
    {
        using var factory = new AnalystWebApplicationFactory();
        using var configured = factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            // Exercise both page modes without contacting a Function or requiring cloud persistence.
            services.AddSingleton<IOptions<FunctionAnalysisOptions>>(Options.Create(new FunctionAnalysisOptions
            {
                Enabled = functionBackend,
                BaseUrl = "https://function.example.test/",
                FunctionKey = "synthetic-function-key",
            }));
            services.AddSingleton(new ApiSettingsStore(Configuration(), new TestEnvironment()));
        }));
        using var client = configured.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost") });
        var document = new HtmlDocument();
        document.LoadHtml(await client.GetStringAsync("/settings"));
        return document;
    }

    private static IConfiguration Configuration() => new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
    {
        ["Api:ApiKey"] = "synthetic-settings-key",
        ["Api:FoundryEndpoint"] = "https://foundry.example.test/api/projects/test",
    }).Build();

    private sealed class TestEnvironment : IWebHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Testing";
        public string ApplicationName { get; set; } = typeof(Settings).Assembly.GetName().Name!;
        public string ContentRootPath { get; set; } = Path.Combine(Path.GetTempPath(), $"settings-test-{Guid.NewGuid():N}");
        public string WebRootPath { get; set; } = "";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
    }

    private sealed class TestNavigationManager : NavigationManager
    {
        public TestNavigationManager() => Initialize("https://localhost/", "https://localhost/settings");
        protected override void NavigateToCore(string uri, bool forceLoad) => throw new NotSupportedException();
    }
}

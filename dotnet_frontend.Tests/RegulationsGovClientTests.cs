using System.Net;
using System.Net.Http.Headers;
using System.IO.Compression;
using System.Text;
using DoedRegulatoryComments.Web.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace DoedRegulatoryComments.Web.Tests;

public class RegulationsGovClientTests
{
    [Fact]
    public async Task FetchCommentsAsync_FetchesEveryPageAndDeduplicatesComments()
    {
        var contentRoot = Path.Combine(Path.GetTempPath(), $"doed-comments-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(contentRoot);

        try
        {
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Api:BaseUrl"] = "https://example.test/v4",
                })
                .Build();
            var environment = new TestHostEnvironment(contentRoot);
            var settings = new ApiSettingsStore(configuration, environment);
            var handler = new QueuedResponseHandler(
                """
                {
                  "data": [
                    { "id": "COMMENT-1", "attributes": { "title": "First", "comment": "Michigan&#39;s Children<br/><br/>Second paragraph." } },
                    { "id": "COMMENT-2", "attributes": { "title": "Second" } }
                  ],
                  "meta": { "numberOfPages": 2, "totalElements": 3 }
                }
                """,
                """
                {
                  "data": [
                    { "id": "COMMENT-2", "attributes": { "title": "Second" } },
                    { "id": "COMMENT-3", "attributes": { "title": "Third" } }
                  ],
                  "meta": { "numberOfPages": 2, "totalElements": 3 }
                }
                """);
            using var http = new HttpClient(handler);
            var client = CreateClient(http, settings);

            var result = await client.FetchCommentsAsync(new FetchCommentsRequest
            {
                DocumentId = "ED-TEST-0001",
                PageSize = 25,
                Page = 99,
            });

            Assert.True(result.Success, result.ErrorMessage);
            Assert.Equal(3, result.Comments.Count);
            Assert.Equal(new[] { "COMMENT-1", "COMMENT-2", "COMMENT-3" }, result.Comments.Select(c => c.Id));
            Assert.Equal($"Michigan's Children{Environment.NewLine}{Environment.NewLine}Second paragraph.", result.Comments[0].Attributes.Comment);
            Assert.Equal(2, handler.RequestedUris.Count);
            Assert.Contains("page%5Bnumber%5D=1", handler.RequestedUris[0].Query);
            Assert.Contains("page%5Bnumber%5D=2", handler.RequestedUris[1].Query);
        }
        finally
        {
            Directory.Delete(contentRoot, recursive: true);
        }
    }

    [Fact]
    public async Task GetCommentAsync_NormalizesCommentHtml()
    {
        var contentRoot = Path.Combine(Path.GetTempPath(), $"doed-comments-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(contentRoot);

        try
        {
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Api:BaseUrl"] = "https://example.test/v4",
                })
                .Build();
            var settings = new ApiSettingsStore(configuration, new TestHostEnvironment(contentRoot));
            var handler = new QueuedResponseHandler(
                """
                {
                  "data": {
                    "id": "COMMENT-1",
                    "attributes": {
                      "comment": "Michigan&#39;s Children<br/><br/>Second paragraph."
                    }
                  }
                }
                """);
            using var http = new HttpClient(handler);
            var client = CreateClient(http, settings);

            var result = await client.GetCommentAsync("COMMENT-1");

            Assert.NotNull(result?.Data);
            Assert.Equal($"Michigan's Children{Environment.NewLine}{Environment.NewLine}Second paragraph.", result.Data.Attributes.Comment);
        }
        finally
        {
            Directory.Delete(contentRoot, recursive: true);
        }
    }

    [Fact]
    public void FetchCommentsRequest_DefaultsToDocketSearch()
    {
        Assert.True(new FetchCommentsRequest().UseDocketFilter);
    }

    [Theory]
    [InlineData("ED-2025-SCC-0481-0001", "ED-2025-SCC-0481")]
    [InlineData("ED-2025-SCC-0481", "ED-2025-SCC-0481")]
    public void ToDocketId_HandlesDocumentAndDocketIds(string input, string expected)
    {
        Assert.Equal(expected, RegulationsGovClient.ToDocketId(input));
    }

    [Theory]
    [InlineData("http://downloads.regulations.gov/file.pdf", "HTTPS")]
    [InlineData("https://example.test/file.pdf", "not allowed")]
    [InlineData("https://downloads.regulations.gov:8443/file.pdf", "standard HTTPS port")]
    public async Task DownloadAttachmentAsync_RejectsUnsafeUrlBeforeRequest(string url, string errorText)
    {
        var handler = new CallbackHandler(_ => throw new InvalidOperationException("HTTP should not be called."));
        using var http = new HttpClient(handler);
        var client = CreateClient(http, NewSettings(), new AttachmentProcessingOptions());

        var result = await client.DownloadAttachmentAsync(url);

        Assert.False(result.Succeeded);
        Assert.Contains(errorText, result.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, handler.RequestCount);
    }

    [Fact]
    public async Task DownloadAttachmentAsync_RejectsIpLiteralEvenWhenConfigured()
    {
        var handler = new CallbackHandler(_ => throw new InvalidOperationException("HTTP should not be called."));
        using var http = new HttpClient(handler);
        var options = new AttachmentProcessingOptions { AllowedHosts = ["127.0.0.1"] };
        var client = CreateClient(http, NewSettings(), options);

        var result = await client.DownloadAttachmentAsync("https://127.0.0.1/file.pdf");

        Assert.False(result.Succeeded);
        Assert.Contains("not an IP address", result.Error);
        Assert.Equal(0, handler.RequestCount);
    }

    [Fact]
    public async Task DownloadAttachmentAsync_AcceptsAllowlistedPdfWithMatchingMimeAndSignature()
    {
        var handler = new CallbackHandler(request => PdfResponse(request, "%PDF-1.7\ncontent"u8.ToArray()));
        using var http = new HttpClient(handler);
        var client = CreateClient(http, NewSettings(), new AttachmentProcessingOptions());

        var result = await client.DownloadAttachmentAsync("https://downloads.regulations.gov/file.pdf");

        Assert.True(result.Succeeded, result.Error);
        Assert.Equal(AttachmentFileKind.Pdf, result.FileKind);
        Assert.Equal("application/pdf", result.MediaType);
    }

    [Fact]
    public async Task DownloadAttachmentAsync_RejectsDeclaredAndStreamedOversizeContent()
    {
        var declaredHandler = new CallbackHandler(request => PdfResponse(request, "%PDF-oversize"u8.ToArray()));
        using var declaredHttp = new HttpClient(declaredHandler);
        var options = new AttachmentProcessingOptions { MaxDownloadBytes = 8 };
        var declaredClient = CreateClient(declaredHttp, NewSettings(), options);

        var declared = await declaredClient.DownloadAttachmentAsync("https://downloads.regulations.gov/file.pdf");

        Assert.False(declared.Succeeded);
        Assert.Contains("8-byte", declared.Error);

        var streamedHandler = new CallbackHandler(request =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = new UnknownLengthContent("%PDF-streamed-oversize"u8.ToArray(), "application/pdf"),
            };
            return response;
        });
        using var streamedHttp = new HttpClient(streamedHandler);
        var streamedClient = CreateClient(streamedHttp, NewSettings(), options);

        var streamed = await streamedClient.DownloadAttachmentAsync("https://downloads.regulations.gov/file.pdf");

        Assert.False(streamed.Succeeded);
        Assert.Contains("8-byte", streamed.Error);
    }

    [Fact]
    public async Task DownloadAttachmentAsync_RejectsMimeSignatureMismatchAndUnsafeRedirect()
    {
        var mismatchHandler = new CallbackHandler(request =>
        {
            var response = PdfResponse(request, "%PDF-1.7"u8.ToArray());
            response.Content.Headers.ContentType = new MediaTypeHeaderValue(
                "application/vnd.openxmlformats-officedocument.wordprocessingml.document");
            return response;
        });
        using var mismatchHttp = new HttpClient(mismatchHandler);
        var mismatchClient = CreateClient(mismatchHttp, NewSettings(), new AttachmentProcessingOptions());

        var mismatch = await mismatchClient.DownloadAttachmentAsync("https://downloads.regulations.gov/file.pdf");

        Assert.False(mismatch.Succeeded);
        Assert.Contains("does not match", mismatch.Error);

        var redirectHandler = new CallbackHandler(request =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.Redirect)
            {
                RequestMessage = request,
            };
            response.Headers.Location = new Uri("https://example.test/redirected.pdf");
            return response;
        });
        using var redirectHttp = new HttpClient(redirectHandler);
        var redirectClient = CreateClient(redirectHttp, NewSettings(), new AttachmentProcessingOptions());

        var redirect = await redirectClient.DownloadAttachmentAsync(
            "https://downloads.regulations.gov/file.pdf");

        Assert.False(redirect.Succeeded);
        Assert.Contains("not allowed", redirect.Error);
        Assert.Equal(1, redirectHandler.RequestCount);
    }

    [Fact]
    public async Task DownloadAttachmentAsync_FollowsOnlyValidatedRedirects()
    {
        var handler = new CallbackHandler(request =>
        {
            if (request.RequestUri!.AbsolutePath == "/initial.pdf")
            {
                var redirect = new HttpResponseMessage(HttpStatusCode.Redirect)
                {
                    RequestMessage = request,
                };
                redirect.Headers.Location = new Uri("/final.pdf", UriKind.Relative);
                return redirect;
            }
            return PdfResponse(request, "%PDF-1.7\nredirected"u8.ToArray());
        });
        using var http = new HttpClient(handler);
        var client = CreateClient(http, NewSettings(), new AttachmentProcessingOptions());

        var result = await client.DownloadAttachmentAsync(
            "https://downloads.regulations.gov/initial.pdf");

        Assert.True(result.Succeeded, result.Error);
        Assert.Equal(2, handler.RequestCount);
    }

    [Fact]
    public async Task DownloadAttachmentAsync_EnforcesOpenXmlEntryAndExpansionLimits()
    {
        var docx = CreateDocxBytes(new string('x', 200));
        var handler = new CallbackHandler(request =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = new ByteArrayContent(docx),
            };
            response.Content.Headers.ContentType = new MediaTypeHeaderValue(
                "application/vnd.openxmlformats-officedocument.wordprocessingml.document");
            return response;
        });
        using var http = new HttpClient(handler);

        var entryLimited = CreateClient(http, NewSettings(), new AttachmentProcessingOptions
        {
            MaxArchiveEntries = 1,
        });
        var tooManyEntries = await entryLimited.DownloadAttachmentAsync(
            "https://downloads.regulations.gov/file.docx");
        Assert.False(tooManyEntries.Succeeded);
        Assert.Contains("entry archive limit", tooManyEntries.Error);

        var expansionLimited = CreateClient(http, NewSettings(), new AttachmentProcessingOptions
        {
            MaxArchiveUncompressedBytes = 100,
        });
        var tooLargeExpanded = await expansionLimited.DownloadAttachmentAsync(
            "https://downloads.regulations.gov/file.docx");
        Assert.False(tooLargeExpanded.Succeeded);
        Assert.Contains("uncompressed limit", tooLargeExpanded.Error);
    }

    private static RegulationsGovClient CreateClient(
        HttpClient http,
        ApiSettingsStore settings,
        AttachmentProcessingOptions? attachmentOptions = null) =>
        new(
            http,
            settings,
            NullLogger<RegulationsGovClient>.Instance,
            Options.Create(attachmentOptions ?? new AttachmentProcessingOptions()));

    private static ApiSettingsStore NewSettings()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Api:BaseUrl"] = "https://example.test/v4",
            })
            .Build();
        return new ApiSettingsStore(
            configuration,
            new TestHostEnvironment(Path.Combine(Path.GetTempPath(), $"doed-comments-tests-{Guid.NewGuid():N}")));
    }

    private static HttpResponseMessage PdfResponse(HttpRequestMessage request, byte[] content)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            RequestMessage = request,
            Content = new ByteArrayContent(content),
        };
        response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
        return response;
    }

    private static byte[] CreateDocxBytes(string documentText)
    {
        using var output = new MemoryStream();
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            var contentTypes = archive.CreateEntry("[Content_Types].xml");
            using (var writer = new StreamWriter(contentTypes.Open(), Encoding.UTF8, leaveOpen: false))
            {
                writer.Write("<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\" />");
            }
            var document = archive.CreateEntry("word/document.xml");
            using var documentWriter = new StreamWriter(document.Open(), Encoding.UTF8, leaveOpen: false);
            documentWriter.Write(documentText);
        }
        return output.ToArray();
    }

    private sealed class QueuedResponseHandler(params string[] responseBodies) : HttpMessageHandler
    {
        private readonly Queue<string> _responseBodies = new(responseBodies);

        public List<Uri> RequestedUris { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestedUris.Add(request.RequestUri!);
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_responseBodies.Dequeue(), Encoding.UTF8, "application/json"),
            };
            return Task.FromResult(response);
        }
    }

    private sealed class CallbackHandler(Func<HttpRequestMessage, HttpResponseMessage> callback) : HttpMessageHandler
    {
        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestCount++;
            return Task.FromResult(callback(request));
        }
    }

    private sealed class UnknownLengthContent : HttpContent
    {
        private readonly byte[] _content;

        public UnknownLengthContent(byte[] content, string mediaType)
        {
            _content = content;
            Headers.ContentType = new MediaTypeHeaderValue(mediaType);
        }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            stream.WriteAsync(_content).AsTask();

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
        }

        protected override Task<Stream> CreateContentReadStreamAsync()
        {
            return Task.FromResult<Stream>(new MemoryStream(_content, writable: false));
        }
    }

    private sealed class TestHostEnvironment(string contentRoot) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Development;
        public string ApplicationName { get; set; } = "DoedRegulatoryComments.Web.Tests";
        public string ContentRootPath { get; set; } = contentRoot;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
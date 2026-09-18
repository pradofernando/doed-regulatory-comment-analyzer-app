using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DoedRegulatoryComments.Web.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace DoedRegulatoryComments.Web.Tests;

public class FunctionAnalysisRunnerTests
{
    [Fact]
    public async Task RunAsync_SubmitsTwoExplicitCommentIds()
    {
        var runId = Guid.NewGuid();
        string? submittedJson = null;
        var handler = new CallbackHandler(async request =>
        {
            if (request.Method == HttpMethod.Post)
            {
                submittedJson = await request.Content!.ReadAsStringAsync();
                return JsonResponse(HttpStatusCode.Accepted, new { runId, status = "queued" });
            }

            return JsonResponse(HttpStatusCode.OK, new { runId, status = "succeeded", errorMessage = (string?)null });
        });
        var expectedRun = new AnalysisRun { DocumentId = "DOC-1", TotalComments = 2, Succeeded = true };
        var runner = CreateRunner(handler, new StubRepository(expectedRun));

        var result = await runner.RunAsync(
            "DOC-1",
            [new CommentResource { Id = "COMMENT-1" }, new CommentResource { Id = "COMMENT-2" }],
            new ApiSettings(),
            progress: null,
            CancellationToken.None);

        Assert.Same(expectedRun, result);
        using var payload = JsonDocument.Parse(Assert.IsType<string>(submittedJson));
        Assert.Equal(
            ["COMMENT-1", "COMMENT-2"],
            payload.RootElement.GetProperty("commentIds").EnumerateArray().Select(item => item.GetString()));
        Assert.Equal(2, payload.RootElement.GetProperty("maxComments").GetInt32());
        Assert.False(payload.RootElement.TryGetProperty("CommentIds", out _));
        Assert.True(handler.SubmittedContentLength > 0);
    }

    [Fact]
    public async Task RunAsync_RejectsBlankCommentIdBeforeSubmission()
    {
        var handler = new CallbackHandler(_ => throw new InvalidOperationException("HTTP should not be called."));
        var runner = CreateRunner(handler, new StubRepository(null));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => runner.RunAsync(
            "DOC-1",
            [new CommentResource { Id = string.Empty }],
            new ApiSettings(),
            progress: null,
            CancellationToken.None));

        Assert.Contains("unique, non-empty comment ID", error.Message);
        Assert.Equal(0, handler.RequestCount);
    }

    private static FunctionAnalysisRunner CreateRunner(HttpMessageHandler handler, IAnalysisRepository repository)
    {
        var client = new HttpClient(handler) { BaseAddress = new Uri("https://functions.example/") };
        var options = Options.Create(new FunctionAnalysisOptions
        {
            PollIntervalSeconds = 0,
            TimeoutMinutes = 1,
        });
        return new FunctionAnalysisRunner(
            client,
            repository,
            options,
            NullLogger<FunctionAnalysisRunner>.Instance);
    }

    private static HttpResponseMessage JsonResponse(HttpStatusCode status, object body) =>
        new(status) { Content = JsonContent.Create(body) };

    private sealed class CallbackHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> callback) : HttpMessageHandler
    {
        public int RequestCount { get; private set; }
        public long? SubmittedContentLength { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestCount++;
            if (request.Method == HttpMethod.Post)
                SubmittedContentLength = request.Content?.Headers.ContentLength;
            return callback(request);
        }
    }

    private sealed class StubRepository(AnalysisRun? run) : IAnalysisRepository
    {
        public Task<AnalysisRun?> LoadRunAsync(Guid id, CancellationToken ct = default) => Task.FromResult(run);
        public Task<Guid> SaveRunAsync(AnalysisRun value, CancellationToken ct = default) => Task.FromResult(Guid.NewGuid());
        public Task AppendFollowUpAsync(Guid runId, FollowUpTurn turn, CancellationToken ct = default) => Task.CompletedTask;
        public Task SetFollowUpThreadAsync(Guid runId, string threadId, CancellationToken ct = default) => Task.CompletedTask;
        public Task RenameRunAsync(Guid runId, string? sessionName, CancellationToken ct = default) => Task.CompletedTask;
        public Task<IReadOnlyList<AnalysisRunSummary>> ListAsync(AnalysisListFilter filter, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<AnalysisRunSummary>>([]);
        public Task DeleteRunAsync(Guid id, CancellationToken ct = default) => Task.CompletedTask;
    }
}
using System.Net;
using System.Text;
using Azure.Core;
using DoedRegulatoryComments.Web.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace DoedRegulatoryComments.Web.Tests;

public class FoundryResponseContractTests
{
    [Fact]
    public void Parser_ReadsFlatTextResponseAndTokenUsage()
    {
        var result = FoundryAnalysisService.ParseFoundryResponseForTesting("""
            {
              "id": "resp-1",
              "output_text": "{\"sentiment\":\"supportive\"}",
              "usage": { "input_tokens": 120, "output_tokens": 30 }
            }
            """);

        Assert.Equal("resp-1", result.ResponseId);
        Assert.Equal("{\"sentiment\":\"supportive\"}", result.Text);
        Assert.Equal(120, result.InputTokens);
        Assert.Equal(30, result.OutputTokens);
    }

    [Fact]
    public void Parser_ReadsCanonicalOutputPartsAndDefaultsMissingUsage()
    {
        var result = FoundryAnalysisService.ParseFoundryResponseForTesting("""
            {
              "id": "resp-2",
              "output": [
                {
                  "content": [
                    { "type": "output_text", "text": "first" },
                    { "type": "text", "text": { "value": " second" } }
                  ]
                }
              ]
            }
            """);

        Assert.Equal("first second", result.Text);
        Assert.Equal(0, result.InputTokens);
        Assert.Equal(0, result.OutputTokens);
    }

    [Fact]
    public void ValidationCorrection_ReplacesGroupingOnlyWhenContractIsValid()
    {
        var original = new GroupedAnalysis
        {
            OverallSummary = "Original",
            OverallSentiment = "mixed",
            ParsedSuccessfully = true,
        };
        original.ThemeGroups.Add(new ThemeGroup
        {
            GroupName = "Original group",
            GroupDescription = "Original description",
            Count = 1,
            SubmissionNumbers = [1],
            StanceDistribution = new Dictionary<string, int> { ["mixed"] = 1 },
        });

        var corrected = FoundryAnalysisService.ApplyValidationResponseForTesting(
            original,
            """
            {
              "status": "corrected",
              "collective_analysis": {
                "overall_summary": "Corrected",
                "theme_groups": [{
                  "group_name": "Corrected group",
                  "group_description": "Corrected description",
                  "count": 1,
                  "submission_numbers": [1],
                  "stance_distribution": { "supportive": 1 },
                  "common_arguments": []
                }],
                "patterns": [],
                "recommendations": [],
                "overall_sentiment": "supportive"
              }
            }
            """,
            expectedTotalComments: 1);

        Assert.Equal("Corrected", corrected.OverallSummary);
        Assert.Equal("Corrected group", corrected.ThemeGroups.Single().GroupName);

        var rejected = FoundryAnalysisService.ApplyValidationResponseForTesting(
            original,
            """{"status":"corrected","collective_analysis":{"overall_summary":"Incomplete"}}""",
            expectedTotalComments: 1);

        Assert.Same(original, rejected);
    }

      [Fact]
      public async Task Client_SendsResponsesApiAgentContractAndParsesResponse()
      {
        var handler = new CapturingHandler();
        using var http = new HttpClient(handler);
        var telemetry = new OperationalTelemetry(Options.Create(new FoundryCostOptions()));
        using var client = new FoundryAnalysisService.FoundryResponsesClient(
          http,
          "https://foundry.example.test/project/",
          new StaticTokenCredential(),
          telemetry);

        var result = await client.CreateResponseAsync(
          "categorization",
          "categorization-agent",
          "v3",
          "synthetic public comment",
          previousResponseId: null,
          CancellationToken.None,
          NullLogger.Instance);

        Assert.Equal("ok", result.Text);
        Assert.Equal("resp-contract", result.ResponseId);
        Assert.Equal(
          "https://foundry.example.test/project/openai/v1/responses",
          handler.RequestUri?.AbsoluteUri);
        Assert.Equal("Bearer", handler.AuthorizationScheme);
        Assert.Equal("test-token", handler.AuthorizationParameter);
        Assert.Contains("\"name\":\"categorization-agent\"", handler.RequestBody);
        Assert.Contains("\"version\":\"v3\"", handler.RequestBody);
        Assert.Contains("\"content\":\"synthetic public comment\"", handler.RequestBody);
        Assert.DoesNotContain("previous_response_id", handler.RequestBody);
      }

      private sealed class StaticTokenCredential : TokenCredential
      {
        private static readonly AccessToken Token = new(
          "test-token",
          DateTimeOffset.UtcNow.AddHours(1));

        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken) => Token;

        public override ValueTask<AccessToken> GetTokenAsync(
          TokenRequestContext requestContext,
          CancellationToken cancellationToken) =>
          ValueTask.FromResult(Token);
      }

      private sealed class CapturingHandler : HttpMessageHandler
      {
        public Uri? RequestUri { get; private set; }
        public string? AuthorizationScheme { get; private set; }
        public string? AuthorizationParameter { get; private set; }
        public string RequestBody { get; private set; } = string.Empty;

        protected override async Task<HttpResponseMessage> SendAsync(
          HttpRequestMessage request,
          CancellationToken cancellationToken)
        {
          RequestUri = request.RequestUri;
          AuthorizationScheme = request.Headers.Authorization?.Scheme;
          AuthorizationParameter = request.Headers.Authorization?.Parameter;
          RequestBody = await request.Content!.ReadAsStringAsync(cancellationToken);
          return new HttpResponseMessage(HttpStatusCode.OK)
          {
            RequestMessage = request,
            Content = new StringContent(
              """
              {
                "id": "resp-contract",
                "output_text": "ok",
                "usage": { "input_tokens": 4, "output_tokens": 1 }
              }
              """,
              Encoding.UTF8,
              "application/json"),
          };
        }
      }
}
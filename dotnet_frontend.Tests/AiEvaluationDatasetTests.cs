using System.Text.Json;
using DoedRegulatoryComments.Web.Services;
using Xunit;

namespace DoedRegulatoryComments.Web.Tests;

public class AiEvaluationDatasetTests
{
    [Fact]
    [Trait("Category", "AiEvaluation")]
    public void SyntheticDataset_MatchesExpectedContracts()
    {
        var fixturePath = Path.Combine(
            AppContext.BaseDirectory,
            "Fixtures",
            "ai-evaluation.v1.json");
        using var fixture = JsonDocument.Parse(File.ReadAllText(fixturePath));
        Assert.Equal(1, fixture.RootElement.GetProperty("schemaVersion").GetInt32());

        foreach (var testCase in fixture.RootElement.GetProperty("categorizationCases").EnumerateArray())
        {
            var name = testCase.GetProperty("name").GetString()!;
            var response = testCase.GetProperty("response").GetString()!;
            var expected = testCase.GetProperty("expectedValid").GetBoolean();
            var parsed = FoundryAnalysisService.ParseCategorizationForTesting(response);
            var evaluation = AnalysisContractValidator.EvaluateCategorization(parsed);
            Assert.True(
                evaluation.IsValid == expected,
                $"Categorization case '{name}' expected valid={expected}. Errors: {string.Join("; ", evaluation.Errors)}");
        }

        foreach (var testCase in fixture.RootElement.GetProperty("groupingCases").EnumerateArray())
        {
            var name = testCase.GetProperty("name").GetString()!;
            var response = testCase.GetProperty("response").GetString()!;
            var totalComments = testCase.GetProperty("totalComments").GetInt32();
            var expected = testCase.GetProperty("expectedValid").GetBoolean();
            var parsed = FoundryAnalysisService.ParseGroupedAnalysisForTesting(response);
            var evaluation = AnalysisContractValidator.EvaluateGroupedAnalysis(parsed, totalComments);
            Assert.True(
                evaluation.IsValid == expected,
                $"Grouping case '{name}' expected valid={expected}. Errors: {string.Join("; ", evaluation.Errors)}");
        }
    }
}
namespace DoedRegulatoryComments.Web.Services;

public interface IFollowUpChatService
{
    Task<string> StartFollowUpThreadAsync(
        AnalysisRun run,
        ApiSettings settings,
        CancellationToken cancellationToken);

    Task<string> AskFollowUpAsync(
        AnalysisRun run,
        string question,
        ApiSettings settings,
        CancellationToken cancellationToken);
}
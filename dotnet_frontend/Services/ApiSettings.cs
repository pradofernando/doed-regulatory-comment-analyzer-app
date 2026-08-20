namespace DoedRegulatoryComments.Web.Services;

/// <summary>
/// Runtime-overridable API configuration for the regulatory comments backend and the AI agents.
/// </summary>
public class ApiSettings
{
    /// <summary>Regulations.gov v4 API base URL. Reference: https://open.gsa.gov/api/regulationsgov/</summary>
    public const string DefaultBaseUrl = "https://api.regulations.gov/v4";

    /// <summary>Foundry project endpoint. Foundry portal → project → "…" menu (top-right) → Project properties → copy the `endpoint` URL. Leave empty so each user/environment fills in their own on the Settings page.</summary>
    public const string DefaultFoundryEndpoint = "";

    /// <summary>Foundry prompt-agent NAME for per-comment categorization (e.g. "RegulatoryCommentCategorizationAgent"). Foundry portal → Agents → row → copy the Name column.</summary>
    public const string DefaultCategorizationAgentName = "RegulatoryCommentCategorizationAgent";

    /// <summary>Foundry prompt-agent NAME for theme grouping + collective analysis.</summary>
    public const string DefaultGroupingAgentName = "RegulatoryCommentGroupingAgent";

    /// <summary>Optional Foundry prompt-agent NAME for validating grouped analysis.</summary>
    public const string DefaultValidationAgentName = "";

    /// <summary>Foundry prompt-agent NAME for the post-analysis follow-up Q&amp;A chat. Optional — leave empty to disable the chat panel.</summary>
    public const string DefaultFollowUpAgentName = "RegulatoryCommentFollowUpAgent";

    /// <summary>Default agent version. "latest" tells the Responses API to use whichever version is currently published.</summary>
    public const string DefaultAgentVersion = "latest";

    /// <summary>Foundry model deployment name (informational only — the prompt agent's own configured model is used at call time).</summary>
    public const string DefaultModelDeploymentName = "gpt-5.4";

    /// <summary>Default comments-per-batch sent to the grouping agent.</summary>
    public const int DefaultBatchSize = 5;

    // Regulations.gov backend
    public string BaseUrl { get; set; } = DefaultBaseUrl;
    public string ApiKey { get; set; } = string.Empty;
    public string DefaultDocumentId { get; set; } = "ED-2025-SCC-0481-0001";

    // Azure AI Foundry prompt agents — invoked through the Responses API.
    // Each agent is identified by NAME + VERSION (no asst_… ID). "latest" version is supported.
    public string FoundryEndpoint { get; set; } = DefaultFoundryEndpoint;

    public string CategorizationAgentName { get; set; } = DefaultCategorizationAgentName;
    public string CategorizationAgentVersion { get; set; } = DefaultAgentVersion;

    public string GroupingAgentName { get; set; } = DefaultGroupingAgentName;
    public string GroupingAgentVersion { get; set; } = DefaultAgentVersion;

    public string ValidationAgentName { get; set; } = DefaultValidationAgentName;
    public string ValidationAgentVersion { get; set; } = DefaultAgentVersion;

    public string FollowUpAgentName { get; set; } = DefaultFollowUpAgentName;
    public string FollowUpAgentVersion { get; set; } = DefaultAgentVersion;

    public string ModelDeploymentName { get; set; } = DefaultModelDeploymentName;
    public int BatchSize { get; set; } = DefaultBatchSize;

    public bool IsUsingDefaultBaseUrl =>
        string.Equals(BaseUrl?.TrimEnd('/'), DefaultBaseUrl.TrimEnd('/'), StringComparison.OrdinalIgnoreCase);

    public bool IsFoundryConfigured =>
        !string.IsNullOrWhiteSpace(FoundryEndpoint)
        && !string.IsNullOrWhiteSpace(CategorizationAgentName)
        && !string.IsNullOrWhiteSpace(GroupingAgentName);

    public bool IsFollowUpChatEnabled => !string.IsNullOrWhiteSpace(FollowUpAgentName);
}

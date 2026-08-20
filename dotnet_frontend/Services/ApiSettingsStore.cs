using System.Text.Json;

namespace DoedRegulatoryComments.Web.Services;

/// <summary>
/// Singleton store that holds the current <see cref="ApiSettings"/> and persists overrides to disk so that
/// they survive an app restart during local testing. Initial values come from appsettings/env, then are merged
/// with any user override file.
/// </summary>
public sealed class ApiSettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly string _overrideFilePath;
    private readonly object _lock = new();
    private ApiSettings _current;

    public ApiSettingsStore(IConfiguration configuration, IHostEnvironment environment)
    {
        _overrideFilePath = Path.Combine(environment.ContentRootPath, "App_Data", "api-settings.json");

        var initial = new ApiSettings();
        configuration.GetSection("Api").Bind(initial);

        // Allow REGULATIONS_GOV_API_KEY env var (matches the Python pipeline) as a fallback for the key.
        if (string.IsNullOrWhiteSpace(initial.ApiKey))
        {
            var envKey = Environment.GetEnvironmentVariable("REGULATIONS_GOV_API_KEY");
            if (!string.IsNullOrWhiteSpace(envKey))
            {
                initial.ApiKey = envKey!;
            }
        }

        // Honor the same Foundry env vars the Python function app uses.
        ApplyEnvOverride(Environment.GetEnvironmentVariable("AZURE_AI_AGENT_ENDPOINT"), v => initial.FoundryEndpoint = v);
        ApplyEnvOverride(Environment.GetEnvironmentVariable("CATEGORIZATION_AGENT_NAME"), v => initial.CategorizationAgentName = v);
        ApplyEnvOverride(Environment.GetEnvironmentVariable("CATEGORIZATION_AGENT_VERSION"), v => initial.CategorizationAgentVersion = v);
        ApplyEnvOverride(Environment.GetEnvironmentVariable("GROUPING_AGENT_NAME"), v => initial.GroupingAgentName = v);
        ApplyEnvOverride(Environment.GetEnvironmentVariable("GROUPING_AGENT_VERSION"), v => initial.GroupingAgentVersion = v);
        ApplyEnvOverride(Environment.GetEnvironmentVariable("VALIDATION_AGENT_NAME"), v => initial.ValidationAgentName = v);
        ApplyEnvOverride(Environment.GetEnvironmentVariable("VALIDATION_AGENT_VERSION"), v => initial.ValidationAgentVersion = v);
        ApplyEnvOverride(Environment.GetEnvironmentVariable("FOLLOWUP_AGENT_NAME"), v => initial.FollowUpAgentName = v);
        ApplyEnvOverride(Environment.GetEnvironmentVariable("FOLLOWUP_AGENT_VERSION"), v => initial.FollowUpAgentVersion = v);
        ApplyEnvOverride(Environment.GetEnvironmentVariable("AZURE_AI_AGENT_MODEL_DEPLOYMENT_NAME"), v => initial.ModelDeploymentName = v);
        var batchEnv = Environment.GetEnvironmentVariable("BATCH_SIZE");
        if (int.TryParse(batchEnv, out var b) && b > 0) initial.BatchSize = b;

        // Layer any persisted override on top of the configured defaults.
        if (File.Exists(_overrideFilePath))
        {
            try
            {
                var json = File.ReadAllText(_overrideFilePath);
                var fromFile = JsonSerializer.Deserialize<ApiSettings>(json);
                if (fromFile is not null)
                {
                    if (!string.IsNullOrWhiteSpace(fromFile.BaseUrl)) initial.BaseUrl = fromFile.BaseUrl;
                    if (!string.IsNullOrWhiteSpace(fromFile.ApiKey)) initial.ApiKey = fromFile.ApiKey;
                    if (!string.IsNullOrWhiteSpace(fromFile.DefaultDocumentId)) initial.DefaultDocumentId = fromFile.DefaultDocumentId;
                    if (!string.IsNullOrWhiteSpace(fromFile.FoundryEndpoint)) initial.FoundryEndpoint = fromFile.FoundryEndpoint;
                    if (!string.IsNullOrWhiteSpace(fromFile.CategorizationAgentName)) initial.CategorizationAgentName = fromFile.CategorizationAgentName;
                    if (!string.IsNullOrWhiteSpace(fromFile.CategorizationAgentVersion)) initial.CategorizationAgentVersion = fromFile.CategorizationAgentVersion;
                    if (!string.IsNullOrWhiteSpace(fromFile.GroupingAgentName)) initial.GroupingAgentName = fromFile.GroupingAgentName;
                    if (!string.IsNullOrWhiteSpace(fromFile.GroupingAgentVersion)) initial.GroupingAgentVersion = fromFile.GroupingAgentVersion;
                    if (!string.IsNullOrWhiteSpace(fromFile.ValidationAgentName)) initial.ValidationAgentName = fromFile.ValidationAgentName;
                    if (!string.IsNullOrWhiteSpace(fromFile.ValidationAgentVersion)) initial.ValidationAgentVersion = fromFile.ValidationAgentVersion;
                    if (!string.IsNullOrWhiteSpace(fromFile.FollowUpAgentName)) initial.FollowUpAgentName = fromFile.FollowUpAgentName;
                    if (!string.IsNullOrWhiteSpace(fromFile.FollowUpAgentVersion)) initial.FollowUpAgentVersion = fromFile.FollowUpAgentVersion;
                    if (!string.IsNullOrWhiteSpace(fromFile.ModelDeploymentName)) initial.ModelDeploymentName = fromFile.ModelDeploymentName;
                    if (fromFile.BatchSize > 0) initial.BatchSize = fromFile.BatchSize;
                }
            }
            catch
            {
                // Ignore — fall back to configured defaults if the override file is corrupt.
            }
        }

        if (string.IsNullOrWhiteSpace(initial.BaseUrl))
        {
            initial.BaseUrl = ApiSettings.DefaultBaseUrl;
        }
        if (initial.BatchSize <= 0)
        {
            initial.BatchSize = ApiSettings.DefaultBatchSize;
        }

        _current = initial;
    }

    public ApiSettings Current
    {
        get
        {
            lock (_lock)
            {
                return Clone(_current);
            }
        }
    }

    public event Action? Changed;

    public void Update(ApiSettings updated)
    {
        ArgumentNullException.ThrowIfNull(updated);

        lock (_lock)
        {
            _current = new ApiSettings
            {
                BaseUrl = string.IsNullOrWhiteSpace(updated.BaseUrl) ? ApiSettings.DefaultBaseUrl : updated.BaseUrl.Trim(),
                ApiKey = updated.ApiKey?.Trim() ?? string.Empty,
                DefaultDocumentId = updated.DefaultDocumentId?.Trim() ?? string.Empty,
                FoundryEndpoint = string.IsNullOrWhiteSpace(updated.FoundryEndpoint) ? ApiSettings.DefaultFoundryEndpoint : updated.FoundryEndpoint.Trim(),
                CategorizationAgentName = string.IsNullOrWhiteSpace(updated.CategorizationAgentName) ? ApiSettings.DefaultCategorizationAgentName : updated.CategorizationAgentName.Trim(),
                CategorizationAgentVersion = string.IsNullOrWhiteSpace(updated.CategorizationAgentVersion) ? ApiSettings.DefaultAgentVersion : updated.CategorizationAgentVersion.Trim(),
                GroupingAgentName = string.IsNullOrWhiteSpace(updated.GroupingAgentName) ? ApiSettings.DefaultGroupingAgentName : updated.GroupingAgentName.Trim(),
                GroupingAgentVersion = string.IsNullOrWhiteSpace(updated.GroupingAgentVersion) ? ApiSettings.DefaultAgentVersion : updated.GroupingAgentVersion.Trim(),
                ValidationAgentName = updated.ValidationAgentName?.Trim() ?? string.Empty,
                ValidationAgentVersion = string.IsNullOrWhiteSpace(updated.ValidationAgentVersion) ? ApiSettings.DefaultAgentVersion : updated.ValidationAgentVersion.Trim(),
                FollowUpAgentName = updated.FollowUpAgentName?.Trim() ?? string.Empty,
                FollowUpAgentVersion = string.IsNullOrWhiteSpace(updated.FollowUpAgentVersion) ? ApiSettings.DefaultAgentVersion : updated.FollowUpAgentVersion.Trim(),
                ModelDeploymentName = string.IsNullOrWhiteSpace(updated.ModelDeploymentName) ? ApiSettings.DefaultModelDeploymentName : updated.ModelDeploymentName.Trim(),
                BatchSize = updated.BatchSize > 0 ? updated.BatchSize : ApiSettings.DefaultBatchSize,
            };

            try
            {
                var dir = Path.GetDirectoryName(_overrideFilePath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                File.WriteAllText(_overrideFilePath, JsonSerializer.Serialize(_current, JsonOptions));
            }
            catch
            {
                // Persist is best-effort; in-memory value is still applied.
            }
        }

        Changed?.Invoke();
    }

    public void ResetToDefaults()
    {
        Update(new ApiSettings());
    }

    private static ApiSettings Clone(ApiSettings s) => new()
    {
        BaseUrl = s.BaseUrl,
        ApiKey = s.ApiKey,
        DefaultDocumentId = s.DefaultDocumentId,
        FoundryEndpoint = s.FoundryEndpoint,
        CategorizationAgentName = s.CategorizationAgentName,
        CategorizationAgentVersion = s.CategorizationAgentVersion,
        GroupingAgentName = s.GroupingAgentName,
        GroupingAgentVersion = s.GroupingAgentVersion,
        ValidationAgentName = s.ValidationAgentName,
        ValidationAgentVersion = s.ValidationAgentVersion,
        FollowUpAgentName = s.FollowUpAgentName,
        FollowUpAgentVersion = s.FollowUpAgentVersion,
        ModelDeploymentName = s.ModelDeploymentName,
        BatchSize = s.BatchSize,
    };

    private static void ApplyEnvOverride(string? value, Action<string> assign)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            assign(value.Trim());
        }
    }
}

namespace DocInt.Api.Configuration;

public sealed class DocIntOptions
{
    public const string SectionName = "DocInt";

    // No property initializers by design: appsettings.json is the single source of truth for
    // the shipped defaults. An omitted or non-positive value fails ValidateOnStart at boot
    // rather than silently falling back to a second set of defaults hidden in this file.
    public long MaxFileBytes { get; set; }
    public int MaxFilesPerRequest { get; set; }
    public int PerFileTimeoutSeconds { get; set; }
    public int MaxParallelism { get; set; }

    /// <summary>Request-level cap: worst-case accepted payload plus multipart overhead.</summary>
    public long MaxRequestBytes => MaxFileBytes * MaxFilesPerRequest + 1_048_576;
}

public sealed class DocumentIntelligenceOptions
{
    public const string SectionName = "DocumentIntelligence";

    public string? Endpoint { get; set; }
    public string? ApiKey { get; set; }
}

public sealed class AzureOpenAIOptions
{
    public const string SectionName = "AzureOpenAI";

    public string? Endpoint { get; set; }
    public string? ApiKey { get; set; }
    // "" is absence, not a default: the shipped name lives only in appsettings.json, and a blank
    // value is legal exactly while no endpoint is configured (the stub-first path).
    public string DeploymentNameVision { get; set; } = "";
}

/// <summary>
/// The boot-time reachability check over the configured Azure endpoints. Nested under DocInt so
/// it travels with the rest of the service's own knobs (DocInt__StartupProbe__Enabled from the
/// environment).
/// </summary>
public sealed class StartupProbeOptions
{
    public const string SectionName = "DocInt:StartupProbe";

    /// <summary>
    /// The off switch. Unlike the numbers below this one carries a default: a bool has no
    /// "absent", and defaulting to false would turn a typo into a silently unverified boot —
    /// exactly the outcome the check exists to prevent. Turn it off where the endpoints are
    /// legitimately unreachable from the host, e.g. a laptop outside the VNet.
    /// </summary>
    public bool Enabled { get; set; } = true;

    // No initializers below, matching DocIntOptions: appsettings.json owns the shipped values, so
    // a section deleted by accident fails ValidateOnStart instead of falling back to a second set
    // of defaults hidden in this file.
    public int Attempts { get; set; }
    public double RetryDelaySeconds { get; set; }
    /// <summary>Ceiling on a single attempt, so one hung TLS handshake cannot starve the rest.</summary>
    public int AttemptTimeoutSeconds { get; set; }
    /// <summary>Ceiling on the whole check. Keep it inside the pod's startupProbe window.</summary>
    public int TotalTimeoutSeconds { get; set; }
}

public static class OptionsExtensions
{
    public static WebApplicationBuilder AddDocIntOptions(this WebApplicationBuilder builder)
    {
        builder.Services.AddOptions<StartupProbeOptions>()
            .Bind(builder.Configuration.GetSection(StartupProbeOptions.SectionName))
            .Validate(o => o.Attempts > 0 && o.AttemptTimeoutSeconds > 0 && o.TotalTimeoutSeconds > 0
                        && o.RetryDelaySeconds >= 0,
                $"{StartupProbeOptions.SectionName} attempts and timeouts must be positive")
            // A budget smaller than the attempts it funds silently truncates the last one, which is
            // the retry that matters — the two before it already failed.
            .Validate(o => o.TotalTimeoutSeconds >= o.Attempts * o.AttemptTimeoutSeconds,
                $"{StartupProbeOptions.SectionName}:TotalTimeoutSeconds must cover "
                + "Attempts x AttemptTimeoutSeconds, or the final attempt is cut short")
            .ValidateOnStart();
        builder.Services.AddOptions<DocIntOptions>()
            .Bind(builder.Configuration.GetSection(DocIntOptions.SectionName))
            .Validate(o => o.MaxFileBytes > 0 && o.MaxFilesPerRequest > 0
                        && o.PerFileTimeoutSeconds > 0 && o.MaxParallelism > 0,
                "DocInt options must all be positive")
            .ValidateOnStart();
        builder.Services.AddOptions<DocumentIntelligenceOptions>()
            .Bind(builder.Configuration.GetSection(DocumentIntelligenceOptions.SectionName))
            .Validate(o => string.IsNullOrWhiteSpace(o.Endpoint) || Uri.TryCreate(o.Endpoint, UriKind.Absolute, out _),
                $"{DocumentIntelligenceOptions.SectionName}:Endpoint must be an absolute URI")
            .ValidateOnStart();
        builder.Services.AddOptions<AzureOpenAIOptions>()
            .Bind(builder.Configuration.GetSection(AzureOpenAIOptions.SectionName))
            .Validate(o => string.IsNullOrWhiteSpace(o.Endpoint) || Uri.TryCreate(o.Endpoint, UriKind.Absolute, out _),
                $"{AzureOpenAIOptions.SectionName}:Endpoint must be an absolute URI")
            .Validate(o => string.IsNullOrWhiteSpace(o.Endpoint) || !string.IsNullOrWhiteSpace(o.DeploymentNameVision),
                $"{AzureOpenAIOptions.SectionName}:DeploymentNameVision is required when "
                + $"{AzureOpenAIOptions.SectionName}:Endpoint is set")
            .ValidateOnStart();
        return builder;
    }
}

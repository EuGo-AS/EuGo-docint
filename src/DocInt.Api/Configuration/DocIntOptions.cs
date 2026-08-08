using Microsoft.Extensions.Options;

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

    /// <summary>
    /// Cap on the sum of accepted file bytes in one request. Distinct from MaxFileBytes x
    /// MaxFilesPerRequest, which is the pathological product of the two (32 x 50 MiB = 1.56 GiB)
    /// and was what MaxRequestBytes used to be — a body 78% the size of the container's entire
    /// memory limit, which Kestrel was configured to accept. Both per-file caps are unchanged;
    /// this bounds their combination.
    /// </summary>
    public long MaxRequestFileBytes { get; set; }

    /// <summary>Request-level cap: accepted file bytes plus multipart framing and the hints part.</summary>
    public long MaxRequestBytes => MaxRequestFileBytes + 1_048_576;
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

/// <summary>
/// The periodic reachability check that keeps /healthz honest after boot. Nested under DocInt
/// alongside StartupProbe (DocInt__DependencyCheck__Enabled from the environment).
/// </summary>
/// <remarks>
/// Deliberately independent of <see cref="StartupProbeOptions"/>. The two run at different
/// times with opposite consequences — boot-time and fatal versus periodic and informational —
/// and deriving one's default from the other's value would be exactly the hidden fallback the
/// comments in this file exist to forbid. A laptop outside the VNet turns off both, explicitly.
/// </remarks>
public sealed class DependencyCheckOptions
{
    public const string SectionName = "DocInt:DependencyCheck";

    /// <summary>
    /// The off switch, carrying its default for the same reason StartupProbe's does: a bool has
    /// no "absent", and defaulting to false would turn a typo into silent non-reporting. False
    /// registers neither the monitor nor the checks, so /healthz reports only "self" — off means
    /// silent, not pinned at "not yet checked".
    /// </summary>
    public bool Enabled { get; set; } = true;

    // No initializers below, matching the classes above: appsettings.json owns the shipped
    // values, so a section deleted by accident fails ValidateOnStart instead of falling back to
    // a second set of defaults hidden in this file.
    public int IntervalSeconds { get; set; }
    /// <summary>Ceiling on one probe. Must fit inside the interval, or ticks overlap.</summary>
    public int TimeoutSeconds { get; set; }
}

/// <summary>
/// Per-pod tracking of repeated file submissions, feeding docint.duplicate_files. Nested under
/// DocInt alongside the other knobs (DocInt__DuplicateTracking__Enabled from the environment).
/// </summary>
/// <remarks>
/// The cache holds 64-bit hashes and nothing else — no bytes, no filenames, nothing
/// reconstructable — so this does not make the service a document store.
/// </remarks>
public sealed class DuplicateTrackingOptions
{
    public const string SectionName = "DocInt:DuplicateTracking";

    /// <summary>
    /// The off switch, carrying its default for the same reason StartupProbe's and
    /// DependencyCheck's do: a bool has no "absent", and defaulting to false would turn a typo
    /// into silent non-measurement. False skips the hashing as well as the accounting — the hash
    /// is the cost — and emits no measurements at all, so a dashboard shows "no data" rather than
    /// a zero that would read as "no duplicates".
    /// </summary>
    public bool Enabled { get; set; } = true;

    // No initializer, matching the classes above: appsettings.json owns the shipped value.
    /// <summary>Distinct hashes retained per pod, FIFO. ~3 MB at 100 000.</summary>
    public int Capacity { get; set; }
}

/// <summary>
/// The per-pod ceiling on bytes held in flight, and what happens when it is reached. Nested under
/// DocInt alongside the other knobs (DocInt__Admission__Enabled from the environment).
/// </summary>
/// <remarks>
/// This is the only thing bounding pod memory. MultipartExtractRequestReader holds every accepted
/// file's byte[] for the whole request and nothing caps concurrent requests, so without it peak
/// memory is bytes-per-request x concurrent-requests against the pod's limit. With it, peak is
/// baseline + BudgetBytes — which is also what makes memory worth autoscaling on.
/// </remarks>
public sealed class AdmissionOptions
{
    public const string SectionName = "DocInt:Admission";

    /// <summary>
    /// The off switch, carrying its default for the same reason the other three do: a bool has no
    /// "absent", and defaulting to false would turn a typo into an unprotected pod. False admits
    /// every request immediately and emits no measurements — it disables admission only, never the
    /// request limits in DocIntOptions.
    /// </summary>
    public bool Enabled { get; set; } = true;

    // No initializers below, matching the classes above: appsettings.json owns the shipped values.
    /// <summary>Bytes a pod will hold in flight at once, across all concurrent requests.</summary>
    public long BudgetBytes { get; set; }
    /// <summary>How long a request waits for budget before it is shed with a 503.</summary>
    public int QueueTimeoutSeconds { get; set; }
    /// <summary>Seconds advertised in the Retry-After header on that 503.</summary>
    public int RetryAfterSeconds { get; set; }
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
        builder.Services.AddOptions<DependencyCheckOptions>()
            .Bind(builder.Configuration.GetSection(DependencyCheckOptions.SectionName))
            .Validate(o => o.IntervalSeconds > 0 && o.TimeoutSeconds > 0,
                $"{DependencyCheckOptions.SectionName} interval and timeout must be positive")
            .Validate(o => o.TimeoutSeconds < o.IntervalSeconds,
                $"{DependencyCheckOptions.SectionName}:TimeoutSeconds must be less than "
                + "IntervalSeconds, or a slow probe overlaps the next tick")
            .ValidateOnStart();
        builder.Services.AddOptions<DuplicateTrackingOptions>()
            .Bind(builder.Configuration.GetSection(DuplicateTrackingOptions.SectionName))
            .Validate(o => o.Capacity > 0,
                $"{DuplicateTrackingOptions.SectionName}:Capacity must be positive")
            .ValidateOnStart();
        builder.Services.AddOptions<DocIntOptions>()
            .Bind(builder.Configuration.GetSection(DocIntOptions.SectionName))
            .Validate(o => o.MaxFileBytes > 0 && o.MaxFilesPerRequest > 0
                        && o.PerFileTimeoutSeconds > 0 && o.MaxParallelism > 0
                        && o.MaxRequestFileBytes > 0,
                "DocInt options must all be positive")
            // A request-total under the per-file cap makes one maximum-size file inadmissible.
            .Validate(o => o.MaxRequestFileBytes >= o.MaxFileBytes,
                $"{DocIntOptions.SectionName}:MaxRequestFileBytes must be at least MaxFileBytes, "
                + "or a single maximum-size file can never be accepted")
            .ValidateOnStart();
        builder.Services.AddOptions<AdmissionOptions>()
            .Bind(builder.Configuration.GetSection(AdmissionOptions.SectionName))
            .Validate(o => o.BudgetBytes > 0 && o.QueueTimeoutSeconds > 0 && o.RetryAfterSeconds > 0,
                $"{AdmissionOptions.SectionName} budget and timings must be positive")
            // Kestrel refuses anything above MaxRequestBytes, so a budget at least that large makes
            // an over-budget request unreachable rather than a case to handle. Without this the
            // limiter would be asked for more permits than it owns and would throw on a live request.
            .Validate<IOptions<DocIntOptions>>((o, docint) =>
            {
                try
                {
                    return o.BudgetBytes >= docint.Value.MaxRequestBytes;
                }
                catch (OptionsValidationException)
                {
                    // DocIntOptions' own ValidateOnStart will report its failure; swallowing here
                    // keeps a broken deployment's log to one clean exception instead of two duplicates.
                    return true;
                }
            },
                $"{AdmissionOptions.SectionName}:BudgetBytes must be at least "
                + "DocInt:MaxRequestBytes, or a legal request could never be admitted")
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

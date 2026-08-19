namespace DocInt.Api.Telemetry;

/// <summary>
/// Writes the effective configuration to the log once at boot, so a pod's log records the limits
/// and endpoints it actually came up with rather than the ones its config file happens to contain.
/// Values are taken after the whole provider chain has resolved; secret-shaped keys are named but
/// never valued.
/// </summary>
public static class StartupConfigurationLog
{
    public const string RedactedValue = "***redacted***";
    private const string EmptyValue = "(empty)";

    /// <summary>
    /// The application's own configuration roots: the top-level sections of appsettings.json, the
    /// host's Logging section, and the two ambient keys that change boot behaviour. The scope
    /// matters because the un-prefixed environment-variable provider folds the entire process
    /// environment into IConfiguration — without this allowlist the dump would be mostly PATH.
    /// </summary>
    private static readonly string[] Roots =
    [
        "DocInt", "Foundry",
        "Serilog", "Logging", "Kestrel", "AllowedHosts",
        "ASPNETCORE_ENVIRONMENT", "OTEL_EXPORTER_OTLP_ENDPOINT",
    ];

    /// <summary>
    /// Substrings that mark a key as carrying a credential. Matched on the last segment, which is
    /// why the Foundry rename needed nothing here: <c>Foundry:ApiKey</c> still ends in "key".
    /// </summary>
    private static readonly string[] SecretMarkers =
    [
        "key", "secret", "password", "pwd", "token", "connectionstring", "sas", "credential",
    ];

    public static WebApplication LogEffectiveConfiguration(this WebApplication app)
    {
        var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("DocInt.Api.Startup");

        var entries = app.Configuration.AsEnumerable()
            .Where(e => e.Value is not null && IsApplicationKey(e.Key))   // null value = section node
            .OrderBy(e => e.Key, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        logger.LogInformation("Effective configuration: {KeyCount} keys", entries.Length);
        foreach (var entry in entries)
        {
            logger.LogInformation("Configuration {Key}={Value}", entry.Key,
                IsSecretKey(entry.Key) ? RedactedValue
                    : entry.Value!.Length == 0 ? EmptyValue
                    : entry.Value);
        }

        return app;
    }

    private static bool IsApplicationKey(string key)
    {
        var separator = key.IndexOf(':');
        var root = separator < 0 ? key.AsSpan() : key.AsSpan(0, separator);
        foreach (var candidate in Roots)
        {
            if (root.Equals(candidate, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    private static bool IsSecretKey(string key)
    {
        var leaf = key.AsSpan(key.LastIndexOf(':') + 1);
        foreach (var marker in SecretMarkers)
        {
            if (leaf.Contains(marker, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }
}

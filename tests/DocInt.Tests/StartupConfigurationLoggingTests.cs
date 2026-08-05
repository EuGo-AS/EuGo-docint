using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace DocInt.Tests;

/// <summary>
/// The boot-time configuration dump. It exists so a deployed pod's log says which limits and
/// endpoints it actually came up with — which means it must carry values, and therefore must
/// never carry a secret's value.
/// </summary>
public class StartupConfigurationLoggingTests
{
    private const string ApiKeySentinel = "SENTINEL-AOAI-KEY-MUST-NOT-BE-LOGGED";
    private const string ForeignSentinel = "SENTINEL-FOREIGN-VALUE-MUST-NOT-BE-LOGGED";

    private sealed class CapturingConfigFactory : DocIntAppFactory
    {
        public CapturingLoggerProvider Capture { get; } = new();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            // A key supplied the way a real deployment supplies it: not from tracked config.
            builder.UseSetting("AzureOpenAI:ApiKey", ApiKeySentinel);
            // Stands in for the process environment, which the un-prefixed environment-variable
            // provider folds into IConfiguration wholesale.
            builder.UseSetting("NotAnAppSection:Whatever", ForeignSentinel);
            base.ConfigureWebHost(builder);
        }

        protected override void ConfigureFakes(IServiceCollection services) =>
            services.AddSingleton<ILoggerProvider>(Capture);
    }

    private static CapturingLoggerProvider BootAndCapture(CapturingConfigFactory factory)
    {
        _ = factory.Services;   // forces the host build, which runs Program up to app.Run()
        return factory.Capture;
    }

    [Fact]
    public void Startup_logs_configuration_values_at_information_level()
    {
        using var factory = new CapturingConfigFactory();
        var capture = BootAndCapture(factory);
        var lines = capture.Lines.ToArray();

        Assert.Contains(lines, l => l.Contains("DocInt:MaxFileBytes") && l.Contains("52428800"));
        Assert.Contains(lines, l => l.Contains("DocInt:MaxParallelism") && l.Contains('4'));
        Assert.Contains(lines, l => l.Contains("AzureOpenAI:DeploymentNameVision")
                                 && l.Contains("model-eugo-docint-vision"));

        var configEntries = capture.Entries.Where(e => e.Message.StartsWith("Configuration ")).ToArray();
        Assert.NotEmpty(configEntries);
        Assert.All(configEntries, e => Assert.Equal(LogLevel.Information, e.Level));
    }

    [Fact]
    public void Startup_configuration_log_names_secret_keys_but_never_their_values()
    {
        using var factory = new CapturingConfigFactory();
        var lines = BootAndCapture(factory).Lines.ToArray();

        Assert.DoesNotContain(lines, l => l.Contains(ApiKeySentinel));
        Assert.Contains(lines, l => l.Contains("AzureOpenAI:ApiKey") && l.Contains("***redacted***"));
    }

    // The dump is scoped to the application's own configuration roots: values still come from
    // every provider, but PATH and the rest of the ambient environment stay out of the log.
    [Fact]
    public void Startup_configuration_log_omits_keys_outside_the_application_sections()
    {
        using var factory = new CapturingConfigFactory();
        var lines = BootAndCapture(factory).Lines.ToArray();

        Assert.DoesNotContain(lines, l => l.Contains(ForeignSentinel));
        Assert.DoesNotContain(lines, l => l.Contains("NotAnAppSection"));
    }
}

using DocInt.Api.Api;
using DocInt.Api.Configuration;
using DocInt.Api.Engines;
using DocInt.Api.Startup;
using DocInt.Api.Telemetry;
using DocInt.Api.Validation;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using Serilog;
using Serilog.Debugging;

Log.Logger = new LoggerConfiguration()
    .Enrich.FromLogContext()
    .WriteTo.Console()
    .CreateBootstrapLogger();
SelfLog.Enable(Console.Error);

try
{
    Log.Information("DocInt host starting");
    var builder = WebApplication.CreateBuilder(args);

    builder.AddServiceDefaults();
    builder.AddDocIntOptions();
    builder.AddStartupConnectivityCheck();

    builder.Services.AddSingleton<DocIntTelemetry>();
    builder.Services.ConfigureOpenTelemetryTracerProvider(t => t.AddSource(DocIntTelemetry.SourceName));
    builder.Services.ConfigureOpenTelemetryMeterProvider(m => m.AddMeter(DocIntTelemetry.MeterName));
    builder.WebHost.ConfigureKestrel((context, kestrel) =>
    {
        var docint = new DocIntOptions();
        context.Configuration.GetSection(DocIntOptions.SectionName).Bind(docint);
        kestrel.Limits.MaxRequestBodySize = docint.MaxRequestBytes;
    });

    builder.Services.AddSingleton<MultipartExtractRequestReader>();
    builder.Services.AddSingleton<EngineRouter>();
    builder.Services.AddSingleton<ExtractionService>();
    builder.Services.AddSingleton<IExtractionEngine, SpreadsheetEngine>();
    builder.Services.AddSingleton<ILayoutAnalysisClient, AzureLayoutAnalysisClient>();
    builder.Services.AddSingleton<IExtractionEngine, LayoutEngine>();
    builder.Services.AddSingleton<IVisionChatClient, AzureVisionChatClient>();
    builder.Services.AddSingleton<IExtractionEngine, VisionEngine>();

    // Serilog replaces the default logging providers; ServiceDefaults' OTel traces/metrics
    // stay active. Under Aspire (OTEL_EXPORTER_OTLP_ENDPOINT set) also ship logs via OTLP.
    var loggerConfiguration = new LoggerConfiguration()
        .ReadFrom.Configuration(builder.Configuration)
        .Enrich.FromLogContext();
    if (!string.IsNullOrWhiteSpace(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]))
    {
        loggerConfiguration.WriteTo.OpenTelemetry();
    }
    var logger = loggerConfiguration.CreateLogger();
    builder.Logging.ClearProviders();
    builder.Logging.AddSerilog(logger);

    builder.Services.AddOpenApi();

    var app = builder.Build();

    // Through ILoggerFactory, not the static Serilog Log: that one is still the bootstrap logger
    // here, so it would bypass the sinks configured above.
    app.LogEffectiveConfiguration();

    if (app.Environment.IsDevelopment())
    {
        app.MapOpenApi();
    }

    app.MapHealthChecks("/healthz");
    app.MapHealthChecks("/alive", new HealthCheckOptions
    {
        Predicate = r => r.Tags.Contains("live")
    });

    app.MapExtract();

    var version = typeof(Program).Assembly
        .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
        .OfType<System.Reflection.AssemblyInformationalVersionAttribute>()
        .FirstOrDefault()?.InformationalVersion ?? "unknown";
    app.MapGet("/info", () => Results.Json(new
    {
        service = "EuGo-docint",
        version,
        endpoints = new[] { "/v1/extract", "/healthz", "/alive", "/info" }
    }));

    app.Run();
}
catch (Exception ex)
{
    Log.Fatal(ex, "DocInt host terminated unexpectedly");
    Environment.ExitCode = 1;
}
finally
{
    Log.CloseAndFlush();
}

public partial class Program;

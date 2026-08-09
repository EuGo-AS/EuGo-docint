using DocInt.Api.Admission;
using DocInt.Api.Api;
using DocInt.Api.Configuration;
using DocInt.Api.Engines;
using DocInt.Api.Health;
using DocInt.Api.Startup;
using DocInt.Api.Telemetry;
using DocInt.Api.Validation;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using OpenTelemetry.Instrumentation.AspNetCore;
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
    builder.Services.AddSingleton<DuplicateFileTracker>();
    builder.Services.ConfigureOpenTelemetryTracerProvider(t => t.AddSource(DocIntTelemetry.SourceName));
    builder.Services.ConfigureOpenTelemetryMeterProvider(m => m.AddMeter(DocIntTelemetry.MeterName));

    // The Prometheus scrape route, read straight from configuration because the exporter has to be
    // registered on the MeterProvider before Build() — well before IOptions is resolvable.
    var metrics = new MetricsOptions();
    builder.Configuration.GetSection(MetricsOptions.SectionName).Bind(metrics);
    if (metrics.Enabled)
    {
        builder.Services.ConfigureOpenTelemetryMeterProvider(m => m.AddPrometheusExporter(o =>
            // No cached body. The exporter's default holds a rendered response for a few hundred
            // milliseconds to absorb scrape storms; nothing scrapes this hard, and a stale answer
            // during an incident is worth more than the CPU it saves.
            o.ScrapeResponseCacheDurationMilliseconds = 0));
        // A scrape every 15s would otherwise mint a span every 15s, forever. Same reasoning as the
        // health endpoints in ServiceDefaults, applied here because only this file knows the path.
        // Composes with that filter rather than replacing it — AddServiceDefaults registered first.
        builder.Services.Configure<AspNetCoreTraceInstrumentationOptions>(o =>
        {
            var inherited = o.Filter;
            o.Filter = context => !context.Request.Path.StartsWithSegments(metrics.Path)
                && (inherited is null || inherited(context));
        });
    }
    builder.WebHost.ConfigureKestrel((context, kestrel) =>
    {
        var docint = new DocIntOptions();
        context.Configuration.GetSection(DocIntOptions.SectionName).Bind(docint);
        kestrel.Limits.MaxRequestBodySize = docint.MaxRequestBytes;
    });

    builder.Services.AddSingleton<MultipartExtractRequestReader>();
    builder.Services.AddSingleton<EngineRouter>();
    builder.Services.AddSingleton<ExtractionService>();
    builder.Services.AddSingleton<RequestAdmissionGate>();
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

    // Two separate options objects, deliberately. /healthz is readiness and carries the
    // dependency report; /alive is liveness and must stay a plain-text, local-only answer —
    // sharing one object here would silently change the liveness body.
    app.MapHealthChecks("/healthz", new HealthCheckOptions
    {
        ResponseWriter = HealthResponseWriter.WriteAsync,
        // Explicit, though these are the framework's defaults: the whole design rests on a
        // failing dependency not evicting the pod, and that must not be an inherited default
        // someone can change without failing a test.
        ResultStatusCodes =
        {
            [HealthStatus.Healthy] = StatusCodes.Status200OK,
            [HealthStatus.Degraded] = StatusCodes.Status200OK,
            [HealthStatus.Unhealthy] = StatusCodes.Status503ServiceUnavailable,
        },
    });
    app.MapHealthChecks("/alive", new HealthCheckOptions
    {
        Predicate = r => r.Tags.Contains("live")
    });

    app.MapExtract();

    if (metrics.Enabled)
    {
        // DisableHttpMetrics keeps the scrape out of http.server.request.duration — without it the
        // route reports on itself, one series per scrape interval that says nothing.
        app.MapPrometheusScrapingEndpoint(metrics.Path).DisableHttpMetrics();
    }

    var version = typeof(Program).Assembly
        .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
        .OfType<System.Reflection.AssemblyInformationalVersionAttribute>()
        .FirstOrDefault()?.InformationalVersion ?? "unknown";
    string[] endpoints = ["/", "/v1/extract", "/healthz", "/alive", "/info",
        .. metrics.Enabled ? new[] { metrics.Path } : []];
    app.MapGet("/info", () => Results.Json(new
    {
        service = "EuGo-docint",
        version,
        endpoints
    }));

    // A bare service banner, so hitting the root in a browser or a curl smoke test names the
    // pod instead of 404ing. Deliberately just the name — /info is where metadata belongs.
    app.MapGet("/", () => Results.Text("EuGo-docint"));

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

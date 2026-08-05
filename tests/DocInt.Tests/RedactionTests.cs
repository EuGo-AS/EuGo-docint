using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace DocInt.Tests;

/// <summary>Captures every formatted log line written through ILogger across all providers.</summary>
public sealed class CapturingLoggerProvider : ILoggerProvider
{
    public ConcurrentQueue<(LogLevel Level, string Message)> Entries { get; } = [];

    /// <summary>The captured messages alone — most assertions only care about the text.</summary>
    public IEnumerable<string> Lines => Entries.Select(e => e.Message);

    public ILogger CreateLogger(string categoryName) => new CapturingLogger(Entries);

    public void Dispose() { }

    private sealed class CapturingLogger(ConcurrentQueue<(LogLevel, string)> entries) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter) =>
            entries.Enqueue((logLevel, formatter(state, exception)));
    }
}

public class RedactionTests
{
    private sealed class CapturingFactory : ContractTestFactory
    {
        public CapturingLoggerProvider Capture { get; } = new();

        protected override void ConfigureFakes(IServiceCollection services)
        {
            base.ConfigureFakes(services);
            services.AddSingleton<ILoggerProvider>(Capture);
        }
    }

    [Fact]
    public async Task Logs_carry_filenames_but_never_document_content_or_extraction_output()
    {
        using var factory = new CapturingFactory();
        using var form = Multipart.Form(("text.pdf", Golden.Bytes("text.pdf"), "application/pdf"));
        var response = await factory.CreateClient().PostAsync("/v1/extract", form);
        response.EnsureSuccessStatusCode();

        var lines = factory.Capture.Lines.ToArray();
        Assert.Contains(lines, l => l.Contains("text.pdf"));
        Assert.DoesNotContain(lines, l => l.Contains("EuGo text PDF golden"));      // document content
        Assert.DoesNotContain(lines, l => l.Contains("LAYOUT_MD_SENTINEL"));        // extraction output
    }
}

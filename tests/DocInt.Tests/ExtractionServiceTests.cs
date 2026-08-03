using System.Diagnostics;
using DocInt.Api.Configuration;
using DocInt.Api.Contracts;
using DocInt.Api.Engines;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace DocInt.Tests;

public class ExtractionServiceTests
{
    private static DocInt.Api.Telemetry.DocIntTelemetry TestTelemetry()
    {
        var provider = new ServiceCollection().AddMetrics().BuildServiceProvider();
        return new DocInt.Api.Telemetry.DocIntTelemetry(
            provider.GetRequiredService<System.Diagnostics.Metrics.IMeterFactory>());
    }

    private sealed class CountingEngine : IExtractionEngine
    {
        private int _inFlight;
        public int MaxObserved;
        public IReadOnlyCollection<FileKind> Kinds { get; } = [FileKind.Pdf];

        public async Task<EngineOutcome> ExtractAsync(FileItem file, CancellationToken ct)
        {
            var now = Interlocked.Increment(ref _inFlight);
            InterlockedExtensions.Max(ref MaxObserved, now);
            await Task.Delay(50, ct);
            Interlocked.Decrement(ref _inFlight);
            return FakeEngine.Markdown(file, $"md-{file.Index}", 1);
        }
    }

    private static class InterlockedExtensions
    {
        public static void Max(ref int location, int value)
        {
            int current;
            while (value > (current = Volatile.Read(ref location)))
                Interlocked.CompareExchange(ref location, value, current);
        }
    }

    [Fact]
    public async Task Respects_parallelism_cap_and_preserves_request_order()
    {
        var options = TestOptions.Wrapped(maxParallelism: 2);
        var engine = new CountingEngine();
        var service = new ExtractionService(
            new EngineRouter([engine], options), options, TestTelemetry(), NullLogger<ExtractionService>.Instance);

        var files = Enumerable.Range(0, 8).Select(i => new FileItem
        {
            Index = i, Name = $"f{i}.pdf", Kind = FileKind.Pdf, Bytes = TestBytes.Pdf
        }).ToArray();

        var response = await service.ExtractAsync(files, CancellationToken.None);

        Assert.Equal(8, response.Files.Count);
        Assert.All(Enumerable.Range(0, 8), i => Assert.Equal($"md-{i}", response.Files[i].Markdown));
        Assert.True(engine.MaxObserved <= 2, $"observed {engine.MaxObserved} concurrent extractions");
        Assert.True(engine.MaxObserved > 1, $"observed {engine.MaxObserved} concurrent extractions — " +
            "expected real parallelism (>1 in flight), not a regression to fully-sequential processing");
    }

    [Fact]
    public async Task Timeout_produces_per_file_timeout_error()
    {
        var options = TestOptions.Wrapped(perFileTimeoutSeconds: 1);
        var hanging = new FakeHangingEngine();
        var service = new ExtractionService(
            new EngineRouter([hanging], options), options, TestTelemetry(), NullLogger<ExtractionService>.Instance);

        var files = new[] { new FileItem { Index = 0, Name = "slow.pdf", Kind = FileKind.Pdf, Bytes = TestBytes.Pdf } };
        var response = await service.ExtractAsync(files, CancellationToken.None);

        Assert.Equal(ErrorCodes.Timeout, response.Files[0].Error!.Code);
    }

    private sealed class FakeHangingEngine : IExtractionEngine
    {
        public IReadOnlyCollection<FileKind> Kinds { get; } = [FileKind.Pdf];
        public async Task<EngineOutcome> ExtractAsync(FileItem file, CancellationToken ct)
        {
            await Task.Delay(TimeSpan.FromMinutes(5), ct);
            throw new UnreachableException();
        }
    }

    [Fact]
    public async Task Stray_operation_canceled_exception_becomes_per_file_engine_error()
    {
        var options = TestOptions.Wrapped();
        var engine = new StrayCancellationEngine();
        var service = new ExtractionService(
            new EngineRouter([engine], options), options, TestTelemetry(), NullLogger<ExtractionService>.Instance);

        var files = new[] { new FileItem { Index = 0, Name = "weird.pdf", Kind = FileKind.Pdf, Bytes = TestBytes.Pdf } };

        // Must not throw out of ExtractAsync (that would 500 the whole batch) — the
        // engine's own OperationCanceledException is unrelated to our per-file timeout
        // token or the request token, so it must be reported as a per-file error.
        var response = await service.ExtractAsync(files, CancellationToken.None);

        Assert.Equal(ErrorCodes.EngineError, response.Files[0].Error!.Code);
    }

    /// <summary>Simulates an engine whose own mechanism (e.g. an SDK client's default
    /// HttpClient.Timeout) throws OperationCanceledException with neither our per-file
    /// timeout token nor the request token cancelled.</summary>
    private sealed class StrayCancellationEngine : IExtractionEngine
    {
        public IReadOnlyCollection<FileKind> Kinds { get; } = [FileKind.Pdf];
        public Task<EngineOutcome> ExtractAsync(FileItem file, CancellationToken ct) =>
            throw new OperationCanceledException();
    }
}

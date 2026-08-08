# DocInt usage counters Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add five OTel instruments to `POST /v1/extract` — files processed by kind and outcome, bytes read, per-file duration, duplicate submissions, and rejected requests by reason — plus the chart values that let them leave the pod.

**Architecture:** All instruments live on the existing `EuGo.DocInt` meter, created in `DocIntTelemetry` and emitted from three code paths that already exist: the per-file body of `ExtractionService`'s `Parallel.ForEachAsync`, the same method after the loop, and `ExtractEndpoint`'s existing `catch (BadExtractRequestException)`. One genuinely new unit, `DuplicateFileTracker`, holds a bounded FIFO of 64-bit content hashes and returns counts without touching the meter.

**Tech Stack:** .NET 10 · `System.Diagnostics.Metrics` (`Counter<long>`, `Histogram<double>`, `InstrumentAdvice<double>`) · `System.IO.Hashing` 10.0.10 (`XxHash64`) · xUnit + `Microsoft.Extensions.Diagnostics.Metrics.Testing` (`MetricCollector<T>`) · Helm.

**Spec:** `docs/superpowers/specs/2026-08-08-docint-usage-counters-design.md` — authoritative; read it first.

## Global Constraints

- **Branch discipline.** All work happens on `feat/usage-counters`, which already exists and already carries the spec commit. Never commit to `main`. Never merge red.
- **The gate, unfiltered, in this exact order, before any merge:**
  `dotnet restore src/DocInt.slnx` → `dotnet build --no-restore src/DocInt.slnx` → `dotnet test --no-build src/DocInt.slnx`. A `--filter` run is fine while iterating on one task but never substitutes for the gate.
- **TDD.** Failing test first, watch it fail for the right reason, then the minimal implementation.
- **Cardinality is closed.** The only tags any instrument may carry are `kind`, `outcome`, `reason`, `scope`. Never a filename, never a hash, never an exception message.
- **No document content in logs, ever.** Filenames, sizes, kinds, durations and outcome codes are fine. Content is not. Hashes are never logged, never a tag, never a trace attribute.
- **No wire change.** No response body, status code, header or OpenAPI document changes anywhere in this plan.
- `net10.0`, `Nullable` and `ImplicitUsings` enabled. Match the surrounding comment density — this codebase explains *why*, not *what*.
- **Use the Context7 MCP server** for any API question about `System.Diagnostics.Metrics`, `System.IO.Hashing` or Helm rather than guessing.

## File Structure

**Created:**

| File | Responsibility |
| --- | --- |
| `src/DocInt.Api/Telemetry/DuplicateFileTracker.cs` | `DuplicateCounts` record + the bounded FIFO hash cache. Pure: no meter, no logger, no HTTP. |
| `tests/DocInt.Tests/DuplicateFileTrackerTests.cs` | Unit tests for the tracker with no host and no DI. |

**Modified:**

| File | Change |
| --- | --- |
| `src/DocInt.Api/Telemetry/DocIntTelemetry.cs` | Five new instruments + their name consts. |
| `src/DocInt.Api/Engines/ExtractionService.cs` | Emits per-file instruments; hashes in-loop; emits duplicates after the loop. |
| `src/DocInt.Api/Engines/FileItem.cs` | `required long SizeBytes`. |
| `src/DocInt.Api/Validation/MultipartExtractRequestReader.cs` | `BufferAsync` returns the observed length; passes a `RejectReasons` code to every throw. |
| `src/DocInt.Api/Validation/HintsParser.cs` | `BadExtractRequestException.Reason`; new `RejectReasons` class; two throws pass `HintsInvalid`. |
| `src/DocInt.Api/Api/ExtractEndpoint.cs` | Takes `DocIntTelemetry`; emits `rejected_requests` in the existing catch. |
| `src/DocInt.Api/Configuration/DocIntOptions.cs` | `DuplicateTrackingOptions` + its registration and validator. |
| `src/DocInt.Api/appsettings.json` | `DocInt:DuplicateTracking` section. |
| `src/DocInt.Api/Program.cs` | Registers `DuplicateFileTracker`. |
| `src/DocInt.Api/DocInt.Api.csproj` | `System.IO.Hashing` 10.0.10. |
| `tests/DocInt.Tests/TelemetryTests.cs` | Tests for all five instruments. |
| `tests/DocInt.Tests/ExtractionServiceTests.cs` | Constructor churn from `SizeBytes` and the tracker. |
| `tests/DocInt.Tests/SpreadsheetEngineTests.cs` | Constructor churn from `SizeBytes`. |
| `tests/DocInt.Tests/OptionsTests.cs` | `DuplicateTracking` defaults and validation. |
| `charts/eugo-docint/values.yaml`, `templates/deployment.yaml`, `Chart.yaml` | `otel.*` values; chart `0.1.3` → `0.1.4`. |
| `.github/workflows/ci.yml` | `chart-lint` assertions for the new env vars. |

**Task order is a dependency chain.** Task 1 must land before Task 2 (`bytes_processed` reads `SizeBytes`). Task 5 must land before Task 6. Everything else is sequential for review convenience.

---

### Task 1: `FileItem.SizeBytes` — an accurate size for over-cap files

`MultipartExtractRequestReader.BufferAsync` returns `([], true)` for a part above `MaxFileBytes`: it drains the rest of the stream and throws the count away. So `file.Bytes.Length` is `0` for a rejected 60 MiB upload, and the existing log line already reports `sizeBytes=0` for exactly the files that cost the most bandwidth. `bytes_processed` in Task 2 would inherit that lie, so it gets fixed first.

**Files:**
- Modify: `src/DocInt.Api/Engines/FileItem.cs`
- Modify: `src/DocInt.Api/Validation/MultipartExtractRequestReader.cs:55`, `:56-62`, `:85`, `:119-135`
- Modify: `src/DocInt.Api/Engines/ExtractionService.cs:26`, `:42`
- Modify: `tests/DocInt.Tests/ExtractionServiceTests.cs:54-57`, `:76`, `:100`
- Modify: `tests/DocInt.Tests/SpreadsheetEngineTests.cs:18`
- Test: `tests/DocInt.Tests/MultipartReaderTests.cs`

**Interfaces:**
- Produces: `FileItem.SizeBytes` (`required long`, `init`) — the total bytes read from the part, including the drained remainder of an over-cap file. Task 2 reads it for `docint.bytes_processed`. `BufferAsync` becomes `Task<(byte[] Bytes, long Observed, bool TooLarge)>`.

- [ ] **Step 1: Write the failing test**

Append to `tests/DocInt.Tests/MultipartReaderTests.cs`, inside the existing test class. It already
has the two helpers these use — `Reader(DocIntOptions?)` and `RequestOf(MultipartFormDataContent)`
— so add no new ones.

```csharp
    // A part above the cap is drained and discarded, so Bytes is empty by design. SizeBytes must
    // still report what arrived: bytes_processed and the per-file log line both read it, and a 0
    // there hides exactly the uploads that consumed the most bandwidth.
    [Fact]
    public async Task Over_cap_file_reports_the_bytes_that_actually_arrived()
    {
        var bom = Golden.Bytes("bom.xlsx");   // 2811 bytes, comfortably over the 1 KiB cap below
        using var form = Multipart.Form(
            ("bom.xlsx", bom, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet"));
        var items = await Reader(TestOptions.DocInt(maxFileBytes: 1024))
            .ReadAsync(await RequestOf(form), CancellationToken.None);

        var file = Assert.Single(items);
        Assert.Equal(ErrorCodes.TooLarge, file.Error!.Code);
        Assert.Empty(file.Bytes);
        Assert.Equal(bom.Length, file.SizeBytes);
    }

    // Control: under the cap, the observed size and the retained buffer agree.
    [Fact]
    public async Task Under_cap_file_reports_its_own_length()
    {
        using var form = Multipart.Form(("a.pdf", TestBytes.Pdf, "application/pdf"));
        var items = await Reader().ReadAsync(await RequestOf(form), CancellationToken.None);

        var file = Assert.Single(items);
        Assert.Null(file.Error);
        Assert.Equal(TestBytes.Pdf.Length, file.SizeBytes);
    }
```

- [ ] **Step 2: Run the tests and watch them fail**

```bash
dotnet build src/DocInt.slnx
dotnet test --no-build src/DocInt.slnx --filter "FullyQualifiedName~MultipartReaderTests"
```

Expected: **compile error** — `FileItem` has no `SizeBytes`. That is the correct failure for this step; the property does not exist yet.

- [ ] **Step 3: Add the property to `FileItem`**

In `src/DocInt.Api/Engines/FileItem.cs`, add below `Bytes`:

```csharp
    /// <summary>
    /// Bytes read from the multipart part, including the drained remainder of an over-cap file.
    /// Not <c>Bytes.Length</c>: a too-large part is discarded, so that would report 0 for exactly
    /// the uploads that consumed the most bandwidth. Required rather than defaulted so the
    /// compiler finds every construction site instead of letting one silently report 0.
    /// </summary>
    public required long SizeBytes { get; init; }
```

- [ ] **Step 4: Make `BufferAsync` return the observed length**

In `src/DocInt.Api/Validation/MultipartExtractRequestReader.cs`, replace the whole `BufferAsync` method:

```csharp
    private static async Task<(byte[] Bytes, long Observed, bool TooLarge)> BufferAsync(
        Stream body, long maxBytes, CancellationToken ct)
    {
        using var buffered = new MemoryStream();
        var buffer = new byte[81920];
        long observed = 0;
        while (true)
        {
            var read = await body.ReadAsync(buffer, ct);
            if (read == 0) break;
            observed += read;
            if (buffered.Length + read > maxBytes)
            {
                // Drain the rest so the reader stays in sync with the multipart framing, and keep
                // counting: the caller reports what arrived even though nothing is retained.
                int drained;
                while ((drained = await body.ReadAsync(buffer, ct)) != 0) observed += drained;
                return ([], observed, true);
            }
            buffered.Write(buffer, 0, read);
        }
        return (buffered.ToArray(), observed, false);
    }
```

Update the two call sites. Line ~55, the file part:

```csharp
                    var (bytes, observed, tooLarge) = await BufferAsync(section.Body, _options.MaxFileBytes, ct);
                    var item = new FileItem
                    {
                        Index = files.Count,
                        Name = fileName,
                        ClaimedContentType = section.ContentType,
                        Bytes = bytes,
                        SizeBytes = observed
                    };
```

Line ~85, the hints part — the observed length is not needed there:

```csharp
                    var (bytes, _, tooLarge) = await BufferAsync(section.Body, MaxHintsBytes, ct);
```

- [ ] **Step 5: Point `ExtractionService` at the new property**

In `src/DocInt.Api/Engines/ExtractionService.cs`, change the trace tag (line ~26) and the log field (line ~42) from `file.Bytes.Length` to `file.SizeBytes`. Both lines, nothing else in the method.

- [ ] **Step 6: Fix the test construction sites the compiler flags**

Four sites. `tests/DocInt.Tests/ExtractionServiceTests.cs` line ~54:

```csharp
        var files = Enumerable.Range(0, 8).Select(i => new FileItem
        {
            Index = i, Name = $"f{i}.pdf", Kind = FileKind.Pdf,
            Bytes = TestBytes.Pdf, SizeBytes = TestBytes.Pdf.Length
        }).ToArray();
```

Lines ~76 and ~100 in the same file:

```csharp
        var files = new[] { new FileItem { Index = 0, Name = "slow.pdf", Kind = FileKind.Pdf,
            Bytes = TestBytes.Pdf, SizeBytes = TestBytes.Pdf.Length } };
```

```csharp
        var files = new[] { new FileItem { Index = 0, Name = "weird.pdf", Kind = FileKind.Pdf,
            Bytes = TestBytes.Pdf, SizeBytes = TestBytes.Pdf.Length } };
```

`tests/DocInt.Tests/SpreadsheetEngineTests.cs` line ~18:

```csharp
        var item = new FileItem { Index = 0, Name = name, Kind = FileKind.Xlsx,
            Bytes = bytes, SizeBytes = bytes.Length };
```

Build after this step and fix any construction site the compiler names that is not in the list above — `required` exists precisely so none can be missed.

- [ ] **Step 7: Run the gate**

```bash
dotnet restore src/DocInt.slnx
dotnet build --no-restore src/DocInt.slnx
dotnet test --no-build src/DocInt.slnx
```

Expected: all green, including the two new tests.

- [ ] **Step 8: Commit**

```bash
git add -A
git commit -m "Report the bytes that actually arrived for an over-cap file

BufferAsync drained an over-cap part and discarded the count, so FileItem.Bytes
was empty and the per-file log line reported sizeBytes=0 for exactly the uploads
that consumed the most bandwidth. SizeBytes now carries the observed length, and
is required so the compiler finds every construction site."
```

---

### Task 2: `docint.files_processed` and `docint.bytes_processed`

**Files:**
- Modify: `src/DocInt.Api/Telemetry/DocIntTelemetry.cs`
- Modify: `src/DocInt.Api/Engines/ExtractionService.cs`
- Test: `tests/DocInt.Tests/TelemetryTests.cs`

**Interfaces:**
- Consumes: `FileItem.SizeBytes` from Task 1.
- Produces: `DocIntTelemetry.FilesProcessedInstrument` = `"docint.files_processed"`, `DocIntTelemetry.BytesProcessedInstrument` = `"docint.bytes_processed"`, and the `Counter<long>` properties `FilesProcessed` and `BytesProcessed`. Tasks 3, 4 and 6 add further instruments to the same class.

- [ ] **Step 1: Write the failing tests**

Append to the `TelemetryTests` class in `tests/DocInt.Tests/TelemetryTests.cs`. The class already has `IClassFixture<ContractTestFactory>` and a `_factory` field.

```csharp
    // The three fixtures cover the three shapes a file can take: extracted (bom.xlsx -> ok),
    // reached an engine and failed (corrupt.xlsx -> corrupt), and rejected by the reader before
    // any engine ran (unknown.bin -> unsupported_type, kind unknown).
    private static MultipartFormDataContent MixedBatch() => Multipart.Form(
        ("bom.xlsx", Golden.Bytes("bom.xlsx"),
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet"),
        ("corrupt.xlsx", Golden.Bytes("corrupt.xlsx"),
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet"),
        ("unknown.bin", Golden.Bytes("unknown.bin"), "application/octet-stream"));

    // Failures count. A "files processed" total that silently drops the corrupt and the
    // unsupported file is not a total, and this is the assertion that keeps the
    // `if (PagesProcessed > 0)` guard from being copied onto this counter.
    [Fact]
    public async Task Files_processed_counts_every_file_including_failures()
    {
        var meterFactory = _factory.Services.GetRequiredService<IMeterFactory>();
        using var collector = new MetricCollector<long>(
            meterFactory, DocIntTelemetry.MeterName, DocIntTelemetry.FilesProcessedInstrument);

        using var form = MixedBatch();
        var response = await _factory.CreateClient().PostAsync("/v1/extract", form);
        response.EnsureSuccessStatusCode();

        var byTag = collector.GetMeasurementSnapshot()
            .ToDictionary(m => ($"{m.Tags["kind"]}/{m.Tags["outcome"]}"), m => m.Value);
        Assert.Equal(3, byTag.Values.Sum());
        Assert.Equal(1, byTag["xlsx/ok"]);
        Assert.Equal(1, byTag[$"xlsx/{ErrorCodes.Corrupt}"]);
        Assert.Equal(1, byTag[$"unknown/{ErrorCodes.UnsupportedType}"]);
    }

    // Bytes read, not bytes successfully extracted: a corrupt file that reports zero would make
    // this useless as a capacity signal.
    [Fact]
    public async Task Bytes_processed_counts_what_was_read_from_every_file()
    {
        var meterFactory = _factory.Services.GetRequiredService<IMeterFactory>();
        using var collector = new MetricCollector<long>(
            meterFactory, DocIntTelemetry.MeterName, DocIntTelemetry.BytesProcessedInstrument);

        using var form = MixedBatch();
        var response = await _factory.CreateClient().PostAsync("/v1/extract", form);
        response.EnsureSuccessStatusCode();

        var expected = Golden.Bytes("bom.xlsx").Length
                     + Golden.Bytes("corrupt.xlsx").Length
                     + Golden.Bytes("unknown.bin").Length;
        var measurements = collector.GetMeasurementSnapshot();
        Assert.Equal(expected, measurements.Sum(m => m.Value));
        Assert.All(measurements, m => Assert.Contains(m.Tags["kind"], new[] { "xlsx", "unknown" }));
    }
```

Add `using DocInt.Api.Contracts;` to the file's usings for `ErrorCodes`.

- [ ] **Step 2: Run and watch them fail**

```bash
dotnet build src/DocInt.slnx
dotnet test --no-build src/DocInt.slnx --filter "FullyQualifiedName~TelemetryTests"
```

Expected: **compile error** — `DocIntTelemetry` has no `FilesProcessedInstrument`.

- [ ] **Step 3: Add the two instruments**

In `src/DocInt.Api/Telemetry/DocIntTelemetry.cs`, add the consts beside `PagesProcessedInstrument`:

```csharp
    public const string FilesProcessedInstrument = "docint.files_processed";
    public const string BytesProcessedInstrument = "docint.bytes_processed";
```

In the constructor, after `PagesProcessed`:

```csharp
        FilesProcessed = _meter.CreateCounter<long>(FilesProcessedInstrument, unit: "files",
            description: "Files processed, by kind and outcome; failures are included");
        BytesProcessed = _meter.CreateCounter<long>(BytesProcessedInstrument, unit: "By",
            description: "Bytes read per file, by kind; counted whether or not extraction succeeded");
```

And the properties beside `PagesProcessed`:

```csharp
    public Counter<long> FilesProcessed { get; }
    public Counter<long> BytesProcessed { get; }
```

- [ ] **Step 4: Emit them**

In `src/DocInt.Api/Engines/ExtractionService.cs`, replace the block from `var outcomeCode = ...` through the `PagesProcessed.Add` call with:

```csharp
                var outcomeCode = outcome.Result.Error?.Code ?? "ok";
                activity?.SetTag("docint.outcome", outcomeCode);

                var kindTag = new KeyValuePair<string, object?>("kind", kindName);
                var outcomeTag = new KeyValuePair<string, object?>("outcome", outcomeCode);
                if (outcome.PagesProcessed > 0)
                    telemetry.PagesProcessed.Add(outcome.PagesProcessed, kindTag);
                // Unconditional, unlike PagesProcessed above — do not copy that guard here. A
                // "files processed" total that drops failures is not a total, and bytes must count
                // what was read, not what came back out.
                telemetry.FilesProcessed.Add(1, kindTag, outcomeTag);
                telemetry.BytesProcessed.Add(file.SizeBytes, kindTag);
```

- [ ] **Step 5: Run the gate**

```bash
dotnet restore src/DocInt.slnx
dotnet build --no-restore src/DocInt.slnx
dotnet test --no-build src/DocInt.slnx
```

Expected: all green.

- [ ] **Step 6: Commit**

```bash
git add -A
git commit -m "Add docint.files_processed and docint.bytes_processed

Both emit unconditionally, unlike pages_processed: a files total that drops
failures is not a total, and bytes count what was read rather than what was
successfully extracted."
```

---

### Task 3: `docint.file_duration`

The loop already measures elapsed time and drops it into a log line. `PerFileTimeoutSeconds` is 100 and is currently tuned against nothing.

**Files:**
- Modify: `src/DocInt.Api/Telemetry/DocIntTelemetry.cs`
- Modify: `src/DocInt.Api/Engines/ExtractionService.cs`
- Test: `tests/DocInt.Tests/TelemetryTests.cs`

**Interfaces:**
- Produces: `DocIntTelemetry.FileDurationInstrument` = `"docint.file_duration"` and the `Histogram<double> FileDuration` property, recording **seconds**.

- [ ] **Step 1: Write the failing test**

Append to `TelemetryTests`:

```csharp
    // Tags and one measurement per file — deliberately no assertion on magnitude, which is
    // timing-dependent and would flake in CI.
    [Fact]
    public async Task File_duration_records_one_measurement_per_file_with_kind_and_outcome()
    {
        var meterFactory = _factory.Services.GetRequiredService<IMeterFactory>();
        using var collector = new MetricCollector<double>(
            meterFactory, DocIntTelemetry.MeterName, DocIntTelemetry.FileDurationInstrument);

        using var form = MixedBatch();
        var response = await _factory.CreateClient().PostAsync("/v1/extract", form);
        response.EnsureSuccessStatusCode();

        var measurements = collector.GetMeasurementSnapshot();
        Assert.Equal(3, measurements.Count);
        Assert.All(measurements, m =>
        {
            Assert.True(m.Value >= 0);
            Assert.NotNull(m.Tags["kind"]);
            Assert.NotNull(m.Tags["outcome"]);
        });
    }
```

- [ ] **Step 2: Run and watch it fail**

```bash
dotnet build src/DocInt.slnx
dotnet test --no-build src/DocInt.slnx --filter "FullyQualifiedName~File_duration_records"
```

Expected: **compile error** — no `FileDurationInstrument`.

- [ ] **Step 3: Add the histogram with explicit buckets**

In `DocIntTelemetry.cs`, add the const:

```csharp
    public const string FileDurationInstrument = "docint.file_duration";
```

and in the constructor:

```csharp
        // Explicit boundaries, in seconds. The .NET defaults run to 10 000 and are shaped for
        // milliseconds; against a PerFileTimeoutSeconds of 100 they would drop nearly every
        // measurement into the first bucket. The top boundary sits above the timeout on purpose,
        // so a measurement that somehow exceeds it stays visible instead of being clamped.
        FileDuration = _meter.CreateHistogram<double>(FileDurationInstrument, unit: "s",
            description: "Wall-clock time to process one file, by kind and outcome",
            advice: new InstrumentAdvice<double>
            {
                HistogramBucketBoundaries = [0.05, 0.1, 0.25, 0.5, 1, 2.5, 5, 10, 25, 50, 100, 250]
            });
```

and the property:

```csharp
    public Histogram<double> FileDuration { get; }
```

`InstrumentAdvice<T>` and the five-argument `CreateHistogram<T>(name, unit, description, tags, advice)` are both part of `System.Diagnostics.Metrics` on `net10.0` — no package reference needed.

- [ ] **Step 4: Record it**

In `ExtractionService.cs`, the elapsed time is currently computed inline inside the log call. Hoist it so it is measured once, then record. Replace the `var started = ...` line's downstream usage: after `var outcomeCode = ...`, add

```csharp
                var elapsed = Stopwatch.GetElapsedTime(started);
```

then add after the `BytesProcessed.Add` line:

```csharp
                telemetry.FileDuration.Record(elapsed.TotalSeconds, kindTag, outcomeTag);
```

and change the final log argument from `Stopwatch.GetElapsedTime(started).TotalMilliseconds` to `elapsed.TotalMilliseconds`. The log line stays in milliseconds on purpose — a different consumer from the histogram, which follows the OTel convention of seconds.

- [ ] **Step 5: Run the gate**

```bash
dotnet restore src/DocInt.slnx
dotnet build --no-restore src/DocInt.slnx
dotnet test --no-build src/DocInt.slnx
```

- [ ] **Step 6: Commit**

```bash
git add -A
git commit -m "Add the docint.file_duration histogram

Seconds, with bucket boundaries shaped for a 100s per-file timeout: the .NET
defaults top out at 10 000 and are built for milliseconds, so every measurement
would land in one bucket."
```

---

### Task 4: `docint.rejected_requests` and the `reason` vocabulary

The 400 path is metrically invisible. ASP.NET Core instrumentation gives a 400 count but never says why.

**Files:**
- Modify: `src/DocInt.Api/Validation/HintsParser.cs` (the exception lives there)
- Modify: `src/DocInt.Api/Validation/MultipartExtractRequestReader.cs` (seven throw sites)
- Modify: `src/DocInt.Api/Api/ExtractEndpoint.cs`
- Modify: `src/DocInt.Api/Telemetry/DocIntTelemetry.cs`
- Test: `tests/DocInt.Tests/TelemetryTests.cs`

**Interfaces:**
- Produces: `DocInt.Api.Validation.RejectReasons` (eight `public const string` fields), `BadExtractRequestException(string reason, string detail)` with a `public string Reason { get; }`, and `DocIntTelemetry.RejectedRequestsInstrument` = `"docint.rejected_requests"` with the `Counter<long> RejectedRequests` property.

- [ ] **Step 1: Write the failing test**

Append to `TelemetryTests`:

```csharp
    // The three shapes are built here rather than inline so the Theory stays readable. Note the
    // hints_invalid case posts a VALID file part alongside the bad hints: ReadAsync checks
    // files.Count == 0 before it ever calls HintsParser, so a request carrying only bad hints is
    // rejected as no_files and this test would assert the wrong reason.
    private static HttpContent BadRequest(string reason) => reason switch
    {
        RejectReasons.NotMultipart => new StringContent("{}", Encoding.UTF8, "application/json"),
        RejectReasons.TooManyFiles => Multipart.Form(Enumerable.Range(0, 33)
            .Select(i => ($"f{i}.pdf", TestBytes.Pdf, "application/pdf")).ToArray()),
        RejectReasons.HintsInvalid => Multipart.Form(("a.pdf", TestBytes.Pdf, "application/pdf"))
            .WithHints("{not json"),
        _ => throw new ArgumentOutOfRangeException(nameof(reason), reason, "no shape for this reason")
    };

    [Theory]
    [InlineData(RejectReasons.NotMultipart)]
    [InlineData(RejectReasons.TooManyFiles)]
    [InlineData(RejectReasons.HintsInvalid)]
    public async Task Rejected_requests_counts_the_400_path_by_reason(string reason)
    {
        var meterFactory = _factory.Services.GetRequiredService<IMeterFactory>();
        using var collector = new MetricCollector<long>(
            meterFactory, DocIntTelemetry.MeterName, DocIntTelemetry.RejectedRequestsInstrument);

        using var content = BadRequest(reason);
        var response = await _factory.CreateClient().PostAsync("/v1/extract", content);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var measurements = collector.GetMeasurementSnapshot();
        Assert.Equal(1, measurements.Sum(m => m.Value));
        Assert.All(measurements, m => Assert.Equal(reason, m.Tags["reason"]));
    }
```

Add `using System.Net;`, `using System.Text;` and `using DocInt.Api.Validation;` to the file's usings.

- [ ] **Step 2: Run and watch it fail**

```bash
dotnet build src/DocInt.slnx
dotnet test --no-build src/DocInt.slnx --filter "FullyQualifiedName~Rejected_requests_counts"
```

Expected: **compile error** — no `RejectReasons`.

- [ ] **Step 3: Add `RejectReasons` and the `Reason` property**

Replace line 6 of `src/DocInt.Api/Validation/HintsParser.cs`:

```csharp
/// <summary>
/// A request-level rejection: the caller's request is malformed, so the whole call is a 400.
/// Distinct from a per-file <see cref="DocInt.Api.Contracts.FileError"/>, which rides inside a
/// 200. Reason is a metric tag, never part of the response body.
/// </summary>
public sealed class BadExtractRequestException(string reason, string detail) : Exception(detail)
{
    public string Reason { get; } = reason;
}

/// <summary>
/// The closed vocabulary behind the `reason` tag on docint.rejected_requests. Every throw site of
/// <see cref="BadExtractRequestException"/> passes one of these; nothing else may become a tag
/// value, or the metric's cardinality stops being bounded.
/// </summary>
public static class RejectReasons
{
    public const string BodyTooLarge = "body_too_large";
    public const string NotMultipart = "not_multipart";
    public const string BoundaryMissing = "boundary_missing";
    public const string TooManyFiles = "too_many_files";
    public const string HintsTooLarge = "hints_too_large";
    public const string HintsInvalid = "hints_invalid";
    public const string MalformedBody = "malformed_body";
    public const string NoFiles = "no_files";
}
```

Then update the two throws inside `HintsParser.Parse` (lines ~18 and ~25) to pass `RejectReasons.HintsInvalid` as the first argument, keeping their existing message as the second.

- [ ] **Step 4: Update the seven reader throw sites**

In `src/DocInt.Api/Validation/MultipartExtractRequestReader.cs`, add the reason as the first argument to each — messages unchanged:

| Line | Reason |
| --- | --- |
| ~27 `request body of {declared} bytes exceeds…` | `RejectReasons.BodyTooLarge` |
| ~32 `request must be multipart/form-data` | `RejectReasons.NotMultipart` |
| ~35 `multipart boundary missing` | `RejectReasons.BoundaryMissing` |
| ~51 `more than {N} files in one request` | `RejectReasons.TooManyFiles` |
| ~87 `hints part exceeds the limit…` | `RejectReasons.HintsTooLarge` |
| ~97 `malformed multipart body` | `RejectReasons.MalformedBody` |
| ~101 `request contains no file parts named 'files'` | `RejectReasons.NoFiles` |

- [ ] **Step 5: Add the instrument**

In `DocIntTelemetry.cs`:

```csharp
    public const string RejectedRequestsInstrument = "docint.rejected_requests";
```

```csharp
        RejectedRequests = _meter.CreateCounter<long>(RejectedRequestsInstrument, unit: "requests",
            description: "Requests rejected as malformed (400), by reason. Not a complete count of "
                + "rejections: a body over the cap with no Content-Length is terminated by Kestrel "
                + "before this code runs");
```

```csharp
    public Counter<long> RejectedRequests { get; }
```

- [ ] **Step 6: Emit it in the endpoint**

In `src/DocInt.Api/Api/ExtractEndpoint.cs`, add `DocIntTelemetry telemetry` to `Handle`'s parameter list and emit inside the existing catch:

```csharp
    private static async Task<IResult> Handle(
        HttpRequest request,
        MultipartExtractRequestReader reader,
        ExtractionService service,
        DocIntTelemetry telemetry,
        CancellationToken ct)
    {
        IReadOnlyList<FileItem> files;
        try
        {
            files = await reader.ReadAsync(request, ct);
        }
        catch (BadExtractRequestException ex)
        {
            telemetry.RejectedRequests.Add(1, new KeyValuePair<string, object?>("reason", ex.Reason));
            // Reason is a metric tag only. The body keeps its existing title and detail — the wire
            // contract does not change.
            return Results.Problem(title: "Malformed extract request", detail: ex.Message,
                statusCode: StatusCodes.Status400BadRequest);
        }
        var response = await service.ExtractAsync(files, ct);
        return Results.Json(response, DocIntJson.Options);
    }
```

Add `using DocInt.Api.Telemetry;` to that file.

- [ ] **Step 7: Run the gate**

```bash
dotnet restore src/DocInt.slnx
dotnet build --no-restore src/DocInt.slnx
dotnet test --no-build src/DocInt.slnx
```

No test constructs `BadExtractRequestException` directly — `HintsParserTests` and `MultipartReaderTests` only assert that it is thrown — so the constructor change breaks no test. If the compiler says otherwise, pass the matching `RejectReasons` value at that site.

- [ ] **Step 8: Commit**

```bash
git add -A
git commit -m "Add docint.rejected_requests with a closed reason vocabulary

The 400 path was metrically invisible: ASP.NET gives a 400 count but never says
why. All nine BadExtractRequestException throw sites now carry one of eight
RejectReasons codes. Reason is a metric tag only; the response body is unchanged."
```

---

### Task 5: `DuplicateTrackingOptions` and `DuplicateFileTracker`

The tracker is pure — no meter, no logger, no HTTP — so it is tested with no host at all.

**Files:**
- Create: `src/DocInt.Api/Telemetry/DuplicateFileTracker.cs`
- Create: `tests/DocInt.Tests/DuplicateFileTrackerTests.cs`
- Modify: `src/DocInt.Api/Configuration/DocIntOptions.cs`
- Modify: `src/DocInt.Api/appsettings.json`
- Modify: `src/DocInt.Api/Program.cs`
- Test: `tests/DocInt.Tests/OptionsTests.cs`

**Interfaces:**
- Produces: `DocInt.Api.Configuration.DuplicateTrackingOptions` with `SectionName = "DocInt:DuplicateTracking"`, `bool Enabled` (code default `true`) and `int Capacity` (from `appsettings.json`); `DocInt.Api.Telemetry.DuplicateCounts(int WithinRequest, int AcrossRequests)`; `DuplicateFileTracker` with `bool Enabled { get; }` and `DuplicateCounts Record(IReadOnlyList<ulong> hashes)`. Task 6 consumes all of these.

- [ ] **Step 1: Write the failing tests**

Create `tests/DocInt.Tests/DuplicateFileTrackerTests.cs`:

```csharp
using DocInt.Api.Configuration;
using DocInt.Api.Telemetry;
using Microsoft.Extensions.Options;

namespace DocInt.Tests;

public class DuplicateFileTrackerTests
{
    private static DuplicateFileTracker Tracker(int capacity = 8) =>
        new(Options.Create(new DuplicateTrackingOptions { Capacity = capacity }));

    [Fact]
    public void Empty_batch_reports_nothing()
    {
        Assert.Equal(new DuplicateCounts(0, 0), Tracker().Record([]));
    }

    [Fact]
    public void Repeats_inside_one_batch_are_counted_once_each_beyond_the_first()
    {
        // Three copies of one hash is two duplicates, not three: the first occurrence is the
        // original. Nothing has been seen before, so the pod count stays 0.
        Assert.Equal(new DuplicateCounts(2, 0), Tracker().Record([7, 7, 7]));
    }

    [Fact]
    public void A_hash_seen_in_an_earlier_batch_counts_against_the_pod_scope()
    {
        var tracker = Tracker();
        Assert.Equal(new DuplicateCounts(0, 0), tracker.Record([1, 2]));
        Assert.Equal(new DuplicateCounts(0, 1), tracker.Record([2, 3]));
    }

    // The two scopes must never both claim the same file. A hash that is repeated within the
    // batch AND was seen before contributes exactly one to each: one repeat inside the batch,
    // one distinct hash that the pod already knew.
    [Fact]
    public void The_two_scopes_do_not_double_count()
    {
        var tracker = Tracker();
        tracker.Record([5]);
        Assert.Equal(new DuplicateCounts(1, 1), tracker.Record([5, 5]));
    }

    // FIFO eviction: with capacity 2, inserting 2 and 3 pushes 1 out, so 1 reads as new again.
    // Without this the cache would grow without bound and the pod's memory limit would decide
    // when tracking stops.
    [Fact]
    public void A_hash_evicted_by_later_inserts_is_no_longer_recognised()
    {
        var tracker = Tracker(capacity: 2);
        tracker.Record([1]);
        tracker.Record([2, 3]);
        Assert.Equal(new DuplicateCounts(0, 0), tracker.Record([1]));
    }

    [Fact]
    public void Enabled_reflects_configuration()
    {
        Assert.True(Tracker().Enabled);
        Assert.False(new DuplicateFileTracker(
            Options.Create(new DuplicateTrackingOptions { Capacity = 8, Enabled = false })).Enabled);
    }
}
```

Also add to `tests/DocInt.Tests/OptionsTests.cs` — a new `[InlineData]` on the existing zero-limit theory, and two facts:

```csharp
    [InlineData("DocInt:DuplicateTracking:Capacity")]
```

```csharp
    [Fact]
    public void Duplicate_tracking_capacity_binds_from_appsettings()
    {
        using var factory = new DocIntAppFactory();
        var o = factory.Services.GetRequiredService<IOptions<DuplicateTrackingOptions>>().Value;
        Assert.Equal(100_000, o.Capacity);
    }

    // Same rule as DependencyCheckOptions: a bool has no "absent", so a missing or misspelled env
    // key must leave tracking on rather than silently switch it off.
    [Fact]
    public void Duplicate_tracking_is_on_unless_something_explicitly_says_otherwise() =>
        Assert.True(new DuplicateTrackingOptions().Enabled);
```

The zero-limit theory asserts `Assert.Contains("positive", ex.Message)`, so the validator's message below must contain the word "positive".

- [ ] **Step 2: Run and watch them fail**

```bash
dotnet build src/DocInt.slnx
dotnet test --no-build src/DocInt.slnx --filter "FullyQualifiedName~DuplicateFileTracker"
```

Expected: **compile error** — neither `DuplicateTrackingOptions` nor `DuplicateFileTracker` exists.

- [ ] **Step 3: Add the options class**

In `src/DocInt.Api/Configuration/DocIntOptions.cs`, after `DependencyCheckOptions`:

```csharp
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
```

and register it in `AddDocIntOptions`, next to the other `AddOptions` calls:

```csharp
        builder.Services.AddOptions<DuplicateTrackingOptions>()
            .Bind(builder.Configuration.GetSection(DuplicateTrackingOptions.SectionName))
            .Validate(o => o.Capacity > 0,
                $"{DuplicateTrackingOptions.SectionName}:Capacity must be positive")
            .ValidateOnStart();
```

- [ ] **Step 4: Add the shipped default to `appsettings.json`**

Inside the `"DocInt"` object, after the `"DependencyCheck"` block:

```json
    ,
    // Per-pod tracking of repeated submissions, behind docint.duplicate_files. The cache holds
    // 64-bit content hashes only — no bytes, no filenames — and is FIFO-evicted at Capacity, so
    // memory is flat (~3 MB at 100000) rather than traffic-dependent.
    // The pod scope is a LOWER BOUND, not a rate: the Service load-balances across replicas, so a
    // repeat lands on the pod that saw it roughly 1/N of the time, and the number moves when the
    // HPA scales. Within-request duplicates are exact and unaffected.
    "DuplicateTracking": {
      "Enabled": true,
      "Capacity": 100000
    }
```

Place the comma correctly — it goes after the closing brace of `"DependencyCheck"`, not before the comment.

- [ ] **Step 5: Write the tracker**

Create `src/DocInt.Api/Telemetry/DuplicateFileTracker.cs`:

```csharp
using DocInt.Api.Configuration;
using Microsoft.Extensions.Options;

namespace DocInt.Api.Telemetry;

/// <summary>Duplicate submissions found in one batch, split by scope. See DuplicateFileTracker.</summary>
public sealed record DuplicateCounts(int WithinRequest, int AcrossRequests);

/// <summary>
/// Counts repeated file submissions from content hashes, holding a bounded FIFO of the hashes this
/// pod has seen. Returns counts and never touches the meter — the caller emits — which keeps this
/// unit testable with no host, no DI and no IMeterFactory.
/// </summary>
/// <remarks>
/// Stores 64-bit hashes only: no bytes, no filenames, nothing reconstructable. The cache is
/// per-pod and resets on restart, which is normal for counters.
/// </remarks>
public sealed class DuplicateFileTracker
{
    private readonly int _capacity;
    private readonly HashSet<ulong> _seen;
    private readonly Queue<ulong> _insertionOrder = new();
    private readonly Lock _gate = new();

    public DuplicateFileTracker(IOptions<DuplicateTrackingOptions> options)
    {
        Enabled = options.Value.Enabled;
        _capacity = options.Value.Capacity;
        // Do not pre-size to Capacity: 100 000 slots would be allocated on every pod that never
        // sees a duplicate. It grows into them.
        _seen = new HashSet<ulong>(capacity: Math.Min(_capacity, 1024));
    }

    public bool Enabled { get; }

    /// <summary>
    /// Accounts one batch. <paramref name="hashes"/> holds one entry per file that reached an
    /// engine — the caller excludes reader-rejected files, whose byte arrays are empty and would
    /// otherwise all hash alike.
    /// </summary>
    public DuplicateCounts Record(IReadOnlyList<ulong> hashes)
    {
        if (hashes.Count == 0) return new DuplicateCounts(0, 0);

        var distinct = new HashSet<ulong>(hashes);
        // Every occurrence beyond the first in its group. Exact, and needs no retained state.
        var withinRequest = hashes.Count - distinct.Count;
        var acrossRequests = 0;

        lock (_gate)
        {
            foreach (var hash in distinct)
            {
                // Counted once per batch per distinct hash, which is what keeps this disjoint from
                // withinRequest above: neither scope can claim the same file.
                if (!_seen.Add(hash))
                {
                    acrossRequests++;
                    continue;
                }
                _insertionOrder.Enqueue(hash);
                // FIFO, not LRU: a hash keeps its original position, so a long-lived popular file
                // ages out like any other. Simpler, and the difference does not matter for a
                // diagnostic counter.
                if (_insertionOrder.Count > _capacity) _seen.Remove(_insertionOrder.Dequeue());
            }
        }

        return new DuplicateCounts(withinRequest, acrossRequests);
    }
}
```

`System.Threading.Lock` is the .NET 9+ dedicated lock type and is what `lock` should target in new code.

- [ ] **Step 6: Register it**

In `src/DocInt.Api/Program.cs`, beside `builder.Services.AddSingleton<DocIntTelemetry>();`:

```csharp
    builder.Services.AddSingleton<DuplicateFileTracker>();
```

Singleton is load-bearing: the cache is the pod's memory of what it has seen, so a scoped or transient registration would reset it every request and `scope=pod` would always read 0.

- [ ] **Step 7: Run the gate**

```bash
dotnet restore src/DocInt.slnx
dotnet build --no-restore src/DocInt.slnx
dotnet test --no-build src/DocInt.slnx
```

- [ ] **Step 8: Commit**

```bash
git add -A
git commit -m "Add DuplicateFileTracker and its options

A bounded FIFO of 64-bit content hashes, returning within-request and per-pod
duplicate counts. Pure: no meter, no logger, no HTTP, so it tests with no host.
Capacity comes from appsettings.json; Enabled defaults true in code, because a
bool has no absent and a typo must not silently stop the measurement."
```

---

### Task 6: Wire duplicates into the request path

**Files:**
- Modify: `src/DocInt.Api/DocInt.Api.csproj`
- Modify: `src/DocInt.Api/Telemetry/DocIntTelemetry.cs`
- Modify: `src/DocInt.Api/Engines/ExtractionService.cs`
- Modify: `tests/DocInt.Tests/ExtractionServiceTests.cs`
- Test: `tests/DocInt.Tests/TelemetryTests.cs`

**Interfaces:**
- Consumes: `DuplicateFileTracker`, `DuplicateCounts` from Task 5.
- Produces: `DocIntTelemetry.DuplicateFilesInstrument` = `"docint.duplicate_files"`, `Counter<long> DuplicateFiles`; `ExtractionService`'s constructor gains a fifth parameter, `DuplicateFileTracker tracker`, placed after `DocIntTelemetry telemetry`.

- [ ] **Step 1: Write the failing tests**

Append to `TelemetryTests`. Note these three build **their own factories** — see the comment.

```csharp
    // Each of these constructs its own factory rather than sharing the class fixture. The pod
    // cache is a singleton for the lifetime of a host, so a golden posted by an earlier test in
    // this class would already be in it and scope=pod would read 1 where the test expects 0 —
    // a failure that depends on test execution order, green one run and red the next.
    private static MultipartFormDataContent Bom(string name = "bom.xlsx") => Multipart.Form(
        (name, Golden.Bytes("bom.xlsx"),
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet"));

    private static long ScopeTotal(IReadOnlyList<CollectedMeasurement<long>> ms, string scope) =>
        ms.Where(m => (string?)m.Tags["scope"] == scope).Sum(m => m.Value);

    // Same content, different filenames: duplicates are detected on bytes, not on names.
    [Fact]
    public async Task Duplicate_files_counts_repeats_inside_one_request()
    {
        using var factory = new ContractTestFactory();
        using var collector = new MetricCollector<long>(
            factory.Services.GetRequiredService<IMeterFactory>(),
            DocIntTelemetry.MeterName, DocIntTelemetry.DuplicateFilesInstrument);

        var bytes = Golden.Bytes("bom.xlsx");
        const string ContentType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";
        using var form = Multipart.Form(("a.xlsx", bytes, ContentType), ("b.xlsx", bytes, ContentType));
        (await factory.CreateClient().PostAsync("/v1/extract", form)).EnsureSuccessStatusCode();

        var measurements = collector.GetMeasurementSnapshot();
        Assert.Equal(1, ScopeTotal(measurements, "request"));
        Assert.Equal(0, ScopeTotal(measurements, "pod"));
    }

    [Fact]
    public async Task Duplicate_files_counts_a_repeat_across_requests_on_the_same_pod()
    {
        using var factory = new ContractTestFactory();
        var client = factory.CreateClient();
        using var collector = new MetricCollector<long>(
            factory.Services.GetRequiredService<IMeterFactory>(),
            DocIntTelemetry.MeterName, DocIntTelemetry.DuplicateFilesInstrument);

        using (var first = Bom()) (await client.PostAsync("/v1/extract", first)).EnsureSuccessStatusCode();
        using (var second = Bom()) (await client.PostAsync("/v1/extract", second)).EnsureSuccessStatusCode();

        var measurements = collector.GetMeasurementSnapshot();
        Assert.Equal(0, ScopeTotal(measurements, "request"));
        Assert.Equal(1, ScopeTotal(measurements, "pod"));
        // Both requests emit, including the one with nothing to report: an enabled tracker
        // produces a zero line, and only a disabled one produces no series at all.
        Assert.Equal(4, measurements.Count);
    }

    // The other half of that contract: off means silent, so a dashboard can tell "not measured"
    // from "measured, none found".
    [Fact]
    public async Task Duplicate_files_emits_nothing_when_tracking_is_disabled()
    {
        using var factory = new NoDuplicateTrackingFactory();
        var client = factory.CreateClient();
        using var collector = new MetricCollector<long>(
            factory.Services.GetRequiredService<IMeterFactory>(),
            DocIntTelemetry.MeterName, DocIntTelemetry.DuplicateFilesInstrument);

        using (var first = Bom()) (await client.PostAsync("/v1/extract", first)).EnsureSuccessStatusCode();
        using (var second = Bom()) (await client.PostAsync("/v1/extract", second)).EnsureSuccessStatusCode();

        Assert.Empty(collector.GetMeasurementSnapshot());
    }

    private sealed class NoDuplicateTrackingFactory : ContractTestFactory
    {
        protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
        {
            builder.UseSetting($"{DuplicateTrackingOptions.SectionName}:Enabled", "false");
            base.ConfigureWebHost(builder);
        }
    }
```

Add `using DocInt.Api.Configuration;` to the file's usings. `CollectedMeasurement<T>` comes from `Microsoft.Extensions.Diagnostics.Metrics.Testing`, already imported.

- [ ] **Step 2: Run and watch them fail**

```bash
dotnet build src/DocInt.slnx
dotnet test --no-build src/DocInt.slnx --filter "FullyQualifiedName~Duplicate_files"
```

Expected: **compile error** — no `DuplicateFilesInstrument`.

- [ ] **Step 3: Add the package reference**

In `src/DocInt.Api/DocInt.Api.csproj`, inside the existing `PackageReference` ItemGroup:

```xml
    <!-- XxHash64 for duplicate-submission counting. Non-cryptographic on purpose: there is no
         security boundary here, only a diagnostic counter, and the 64-bit output is exactly what
         the tracker's cache stores. -->
    <PackageReference Include="System.IO.Hashing" Version="10.0.10" />
```

- [ ] **Step 4: Add the instrument**

In `DocIntTelemetry.cs`:

```csharp
    public const string DuplicateFilesInstrument = "docint.duplicate_files";
```

```csharp
        DuplicateFiles = _meter.CreateCounter<long>(DuplicateFilesInstrument, unit: "files",
            description: "Repeated file submissions. scope=request is exact. scope=pod is a LOWER "
                + "BOUND, not a rate: the Service load-balances across replicas, so a repeat lands "
                + "on the pod that saw it roughly 1/N of the time, and the value moves when the "
                + "HPA scales");
```

```csharp
    public Counter<long> DuplicateFiles { get; }
```

The caveat lives in the description, not only in the spec, so whoever finds this in a metrics browser sees it there.

- [ ] **Step 5: Hash in the loop and emit after it**

In `src/DocInt.Api/Engines/ExtractionService.cs`, add `using System.IO.Hashing;` and add `DuplicateFileTracker tracker` to the primary constructor, immediately after `DocIntTelemetry telemetry`.

Inside `ExtractAsync`, alongside `var results = ...`:

```csharp
        // Indexed by file.Index, null where the file is excluded from tracking. Kept local rather
        // than on FileItem: a hash exists only to feed a counter, and FileItem models the file as
        // it moves through validation and routing.
        var hashes = new ulong?[files.Count];
```

Inside the loop body, after the activity tags and before `var started = ...`:

```csharp
                // Hashed here rather than in a pass before the loop. The pod scope needs every
                // accepted file hashed — a file unique within its batch still has to be checked
                // against the cache — so grouping by length first saves nothing, and 32 x 50 MiB
                // on the critical path is ~300ms single-threaded. In here it runs at
                // MaxParallelism and, for every engine but the synchronous SpreadsheetEngine,
                // hides behind an Azure round-trip.
                // Files with an Error are excluded and this is load-bearing: a too_large file
                // carries an empty Bytes, so without the check every over-cap file in a batch
                // would hash alike and be reported as a duplicate of the others.
                if (tracker.Enabled && file.Error is null)
                    hashes[file.Index] = XxHash64.HashToUInt64(file.Bytes);
```

And after `Parallel.ForEachAsync` returns, before `return new ExtractResponse(results);`:

```csharp
        // After the loop, so it is skipped when the loop throws — EngineRouter rethrows on genuine
        // request abandonment, and an abandoned request has no meaningful outcome to report.
        if (tracker.Enabled)
        {
            var counts = tracker.Record([.. hashes.Where(h => h.HasValue).Select(h => h!.Value)]);
            // Emitted even at zero: an enabled tracker with nothing to report must produce a flat
            // zero line, so a dashboard can tell it apart from a tracker that is switched off.
            telemetry.DuplicateFiles.Add(counts.WithinRequest,
                new KeyValuePair<string, object?>("scope", "request"));
            telemetry.DuplicateFiles.Add(counts.AcrossRequests,
                new KeyValuePair<string, object?>("scope", "pod"));
        }
```

- [ ] **Step 6: Fix the three direct constructions in `ExtractionServiceTests`**

Lines ~51, ~73 and ~97 construct `ExtractionService` with four arguments. Add a helper to the class and use it at all three sites:

```csharp
    private static DuplicateFileTracker TestTracker() =>
        new(Options.Create(new DuplicateTrackingOptions { Capacity = 16 }));
```

```csharp
        var service = new ExtractionService(
            new EngineRouter([engine], options), options, TestTelemetry(), TestTracker(),
            NullLogger<ExtractionService>.Instance);
```

Add `using DocInt.Api.Telemetry;` if the file does not already have it.

- [ ] **Step 7: Run the gate**

```bash
dotnet restore src/DocInt.slnx
dotnet build --no-restore src/DocInt.slnx
dotnet test --no-build src/DocInt.slnx
```

- [ ] **Step 8: Commit**

```bash
git add -A
git commit -m "Add docint.duplicate_files with request and pod scopes

Hashed inside the parallel loop, not in a pass before it: pod-scope needs every
accepted file hashed, so length-grouping saves nothing and 1.6 GiB on the
critical path does not. Reader-rejected files are excluded — a too_large file
carries empty bytes and would otherwise hash alike.

The pod scope is a lower bound scaled by roughly 1/replicas; that caveat is in
the instrument description, not only the spec."
```

---

### Task 7: Chart values for the OTLP export path

Every instrument above is unreadable in AKS: OTel exports only when `OTEL_EXPORTER_OTLP_ENDPOINT` is set and the chart has no value for it. This task closes the chart half. Standing a collector up is EuGo-infra's work and is out of scope.

**Files:**
- Modify: `charts/eugo-docint/values.yaml`
- Modify: `charts/eugo-docint/templates/deployment.yaml`
- Modify: `charts/eugo-docint/Chart.yaml`
- Modify: `.github/workflows/ci.yml`

**Interfaces:**
- Produces: values `otel.endpoint` and `otel.protocol`, both empty by default; env vars `OTEL_EXPORTER_OTLP_ENDPOINT`, `OTEL_EXPORTER_OTLP_PROTOCOL`, `OTEL_SERVICE_NAME`.

- [ ] **Step 1: Write the failing check**

There is no unit-test harness for the chart; `chart-lint` in CI is the harness, and it asserts by rendering and grepping. Add this to `.github/workflows/ci.yml` in the `chart-lint` job, after the existing "zero limits reach the pod" step:

```yaml
      # The counters added in this release are invisible without an OTLP endpoint, so the chart
      # has to be able to set one. Both halves are asserted: set renders all three vars (the
      # service name is derived, not user-supplied, and without it every namespace reports as
      # DocInt.Api), and unset renders none of them -- a release that configures no collector
      # must behave exactly as it did before.
      - name: otel env renders only when an endpoint is set
        run: |
          render() { helm template ci charts/eugo-docint --set image.repository=r.example/eugo-docint "$@"; }
          render --set otel.endpoint=http://collector:4317 --set otel.protocol=grpc > otel-on.yaml
          grep -q 'OTEL_EXPORTER_OTLP_ENDPOINT' otel-on.yaml
          grep -q 'OTEL_EXPORTER_OTLP_PROTOCOL' otel-on.yaml
          grep -q 'OTEL_SERVICE_NAME' otel-on.yaml
          render > otel-off.yaml
          ! grep -q 'OTEL_' otel-off.yaml
```

- [ ] **Step 2: Run it and watch it fail**

```bash
helm template ci charts/eugo-docint --set image.repository=r.example/eugo-docint --set otel.endpoint=http://collector:4317 | grep OTEL_
```

Expected: no output, exit 1 — the values do not exist yet.

- [ ] **Step 3: Add the values**

In `charts/eugo-docint/values.yaml`, after the `docint:` block and before `extraEnv`:

```yaml
# Telemetry export. Empty endpoint omits all three vars below, which is the shipped behaviour:
# traces and metrics are collected in-process and go nowhere. Set it to an OTLP collector to
# ship them. The counters on /v1/extract are unreadable outside the pod without this.
otel:
  # Sets OTEL_EXPORTER_OTLP_ENDPOINT, e.g. http://otel-collector.observability:4317
  endpoint: ""
  # Sets OTEL_EXPORTER_OTLP_PROTOCOL. The .NET exporter defaults to grpc; many collectors accept
  # only http/protobuf, so this is first-class rather than left to extraEnv.
  protocol: ""
```

- [ ] **Step 4: Render them**

In `charts/eugo-docint/templates/deployment.yaml`, insert immediately before the `{{- $env = concat $env .Values.extraEnv }}` line:

```yaml
          {{- /* OTEL_SERVICE_NAME is derived rather than user-supplied: without it every release
                 reports as the assembly name (DocInt.Api) and two namespaces cannot be told
                 apart. Rendered only alongside an endpoint -- with no exporter configured the var
                 would be inert clutter. `$` inside the `with` block because `.` is rebound to the
                 endpoint string. */}}
          {{- with .Values.otel.endpoint }}
          {{- $env = append $env (dict "name" "OTEL_EXPORTER_OTLP_ENDPOINT" "value" .) }}
          {{- $env = append $env (dict "name" "OTEL_SERVICE_NAME" "value" (include "eugo-docint.fullname" $)) }}
          {{- with $.Values.otel.protocol }}
          {{- $env = append $env (dict "name" "OTEL_EXPORTER_OTLP_PROTOCOL" "value" .) }}
          {{- end }}
          {{- end }}
```

`with` is correct here, unlike for the numeric limits above it: these are strings, so there is no 0-is-empty trap.

- [ ] **Step 5: Bump the chart version**

In `charts/eugo-docint/Chart.yaml`, change `version: 0.1.3` to `version: 0.1.4`. Leave `appVersion` exactly as it is — CI stamps it at package time.

A **patch**, not a minor: the three env vars are read by ServiceDefaults, which is already in the shipped image, so this chart works against every `0.1.x` build. That is the "several chart versions per image is normal" case in the `Chart.yaml` contract. A minor bump here would put the chart's minor ahead of the image's until a `v0.2.0` tag existed, breaking the `major.minor` invariant with nothing in `chart-lint` to catch it. When the image release carrying these counters is cut, that release moves image and chart to `0.2.0` together.

- [ ] **Step 6: Verify locally**

```bash
helm lint charts/eugo-docint
helm template ci charts/eugo-docint -f charts/eugo-docint/ci/test-values.yaml > /dev/null
helm template ci charts/eugo-docint --set image.repository=r.example/eugo-docint --set otel.endpoint=http://collector:4317 --set otel.protocol=grpc | grep OTEL_
helm template ci charts/eugo-docint --set image.repository=r.example/eugo-docint | grep OTEL_ || echo "correctly absent"
```

Expected: three `OTEL_*` names in the first grep; `correctly absent` from the second.

- [ ] **Step 7: Run the full gate**

```bash
dotnet restore src/DocInt.slnx
dotnet build --no-restore src/DocInt.slnx
dotnet test --no-build src/DocInt.slnx
```

The chart change touches no C#, but the gate runs unfiltered before any merge.

- [ ] **Step 8: Commit**

```bash
git add -A
git commit -m "Chart: first-class OTLP endpoint, protocol and service name

Without an endpoint the counters never leave the pod, and the chart had only the
verbatim extraEnv escape hatch. All three vars render only when an endpoint is
set, so a release that configures no collector behaves exactly as before.

Chart 0.1.3 -> 0.1.4, a patch: ServiceDefaults in the shipped image already
reads these vars, so this chart works against any 0.1.x build. The minor bump to
0.2.0 belongs to the image release, not here."
```

---

## Finishing

After Task 7 is green, use **superpowers:finishing-a-development-branch** with base branch `main`.

Before that skill's menu, re-read the two things this plan cannot verify offline:

1. **`README.md`** — check whether it documents the metric surface. If it lists `docint.pages_processed`, add the five new instruments there in the same style, in a final documentation commit.
2. **`CLAUDE.md`** — its Architecture section names `Telemetry/` as "Serilog config, pages-processed metric". Update that phrase to cover the counters, in the same commit.

Neither is a code change and neither has a test; they are the parts of the repo that go stale silently.

## Spec coverage

| Spec section | Task |
| --- | --- |
| §1 instrument set, tag vocabularies | 2, 3, 4, 6 |
| §1 histogram bucket boundaries | 3 |
| §1 no request-count instrument | n/a — deliberate omission, nothing to build |
| §2.1 unconditional emission, no `PagesProcessed` guard | 2 |
| §2.2 emission after the loop | 6 |
| §2.3 rejected requests in the endpoint catch | 4 |
| §2.4 `BadExtractRequestException.Reason`, `RejectReasons` | 4 |
| §2.5 `FileItem.SizeBytes`, `BufferAsync` | 1 |
| §3.1 two disjoint scopes, `Error is null` exclusion | 5 (accounting), 6 (exclusion) |
| §3.2 lower-bound caveat in the description | 6 |
| §3.3 storage-free compliance | 5 |
| §3.4 hash inside the loop | 6 |
| §3.5 tracker shape, FIFO cache, zeros emitted | 5, 6 |
| §3.6 `XxHash64`, `System.IO.Hashing` 10.0.10 | 6 |
| §4 `DuplicateTrackingOptions`, the `Enabled`/`Capacity` split | 5 |
| §5 chart values, `OTEL_SERVICE_NAME`, version bump, CI | 7 |
| §6 cardinality, no content in logs, no wire change | Global Constraints; enforced per task |
| §7 all listed tests | 1–7, each in its own task |
| §8 deferred items | n/a — deliberately not built |

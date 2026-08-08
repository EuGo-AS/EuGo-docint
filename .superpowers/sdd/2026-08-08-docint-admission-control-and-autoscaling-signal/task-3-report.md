# Task 3: AdmissionOptions Configuration - Report

## Status
DONE

## Changes Per File

### src/DocInt.Api/Configuration/DocIntOptions.cs
- Added `using Microsoft.Extensions.Options;` to support dependency-injected validation
- Added `AdmissionOptions` sealed class with:
  - `const string SectionName = "DocInt:Admission"`
  - `bool Enabled { get; set; } = true` (defaults to on; a bool has no "absent")
  - `long BudgetBytes { get; set; }` (no initializer; appsettings.json owns the shipped value)
  - `int QueueTimeoutSeconds { get; set; }` (ditto)
  - `int RetryAfterSeconds { get; set; }` (ditto)
  - XML comments explaining the per-pod memory boundary and defaults
- Registered `AdmissionOptions` in `AddDocIntOptions()` extension method after `DocIntOptions`, with:
  - Binding to `DocInt:Admission` configuration section
  - Positivity validator for `BudgetBytes`, `QueueTimeoutSeconds`, `RetryAfterSeconds`
  - Cross-validator `Validate<IOptions<DocIntOptions>>` enforcing `BudgetBytes >= MaxRequestBytes`, wrapped in try-catch
    - Catches `OptionsValidationException` from `docint.Value` dereference when DocIntOptions is invalid
    - Returns `true` (validator passes) because DocIntOptions' own `ValidateOnStart` reports the failure
    - Prevents StartupValidator from collecting two identical exceptions and emitting `AggregateException`
  - `ValidateOnStart()` to fail boot if violated

### src/DocInt.Api/appsettings.json
- Added `"Admission"` block inside `"DocInt"` section with:
  - `"Enabled": true`
  - `"BudgetBytes": 1073741824` (1 GiB; meets spec requirement >= MaxRequestBytes of 210,763,776)
  - `"QueueTimeoutSeconds": 10`
  - `"RetryAfterSeconds": 5`
  - Multi-line comments explaining the role in memory bounding and the invariant

### tests/DocInt.Tests/OptionsTests.cs
- **Reverted** `Validate()` helper to its exact original form (no defaults injection, no exception unwrapping)
  - This restores regression coverage: if the Admission section were accidentally deleted from appsettings.json, bare `Validate()` calls would now correctly fail instead of silently passing on shadow hardcoded defaults
  - The cross-validator tolerance fix in production code (try-catch in DocIntOptions.cs) eliminates the need for exception unwrapping in tests
- Removed `using System.Linq;` (no longer needed after reverting Validate())
- Added three new `[Fact]` tests:
  - `Admission_defaults_bind_from_appsettings()` — verifies shipped values bind correctly
  - `Admission_is_on_unless_something_explicitly_says_otherwise()` — verifies default `Enabled = true`
  - `Budget_below_the_largest_admissible_request_fails_validation()` — verifies boot fails when BudgetBytes < MaxRequestBytes
- Added two new cases to `Zero_limit_fails_host_startup` theory:
  - `"DocInt:Admission:BudgetBytes"`
  - `"DocInt:Admission:QueueTimeoutSeconds"`

## Test Results (Final)
```
Passed!  - Failed: 0, Passed: 144, Skipped: 7, Total: 151, Duration: 1 s
```

Command: `dotnet restore src/DocInt.slnx && dotnet build --no-restore src/DocInt.slnx && dotnet test --no-build src/DocInt.slnx`

Breakdown:
- 139 pre-existing tests: all passing
- 3 new Admission facts: passing
- 2 new Admission theory rows (in Zero_limit_fails_host_startup): passing
- 7 skipped live-Azure smoke tests (env-gated, as expected)

No deviations from brief in final version.

---

## Review Findings & Fixes (Post-Initial Submission)

Reviewer identified two important findings in the initial test-helper modification approach:

**Finding 1:** The Admission-defaults injection in `Validate()` (lines 90-97) was unnecessary and masked a regression.
- `WebApplication.CreateBuilder()` already loads appsettings.json, which supplies the valid defaults.
- Hardcoded shadow defaults in test code violated the anti-pattern that DocIntOptions itself forbids in production code.
- **Fix:** Reverted `Validate()` to original form. If the Admission section were accidentally deleted from appsettings.json, bare `Validate()` calls would now correctly fail instead of silently passing.

**Finding 2:** The AggregateException unwrapping in the test helper (lines 104-118) was a workaround; a cleaner fix exists in production code.
- When DocIntOptions is invalid and the cross-validator dereferences `docint.Value`, it re-surfaces DocInt's `OptionsValidationException`. StartupValidator collects two identical exceptions and throws `AggregateException`.
- **Fix:** Wrapped the dereference in try-catch in `DocIntOptions.cs` cross-validator (line 210+). Catches `OptionsValidationException` and returns `true` (validator passes) because DocIntOptions' own `ValidateOnStart` reports the failure. Keeps the pod's startup log clean: one exception instead of two duplicates.
- **Result:** Test helper no longer needs exception unwrapping; reverted to original, simple form.

**Commit:** `e7ce690` — Fix cross-validator to tolerate invalid DocIntOptions dependency

## Follow-up Items (Out of Scope)

1. **Operator Configuration Risk:** An operator who sets `DocInt:MaxRequestFileBytes` above 1 GiB minus 1 MiB in Helm values will cause pod boot failure. The chart values documentation or runbook should clarify this constraint.
2. **No Gate Application Yet:** This task adds only configuration and validation. The gate component that *uses* AdmissionOptions (RequestAdmissionGate, endpoint filter, telemetry) is deferred to the next task per instructions.

## Implementation Notes

- The cross-validator tries to read `docint.Value` to compute the maximum request bytes. If DocIntOptions validation has failed, accessing `.Value` throws `OptionsValidationException`. The production-code fix (try-catch) tolerates this by returning `true` (validator passes), because DocIntOptions' own `ValidateOnStart` will report the real error. This keeps the pod's startup log clean: one exception instead of two duplicates.
- `WebApplication.CreateBuilder()` in the test helper automatically loads `appsettings.json` from the content root, so the Admission section is available even without explicit defaults injection. Removing the shadow defaults restores regression coverage: if the section were deleted, tests would correctly fail.
- The computed property `MaxRequestBytes` (= `MaxRequestFileBytes + 1_048_576`) accounts for multipart framing and hints and is the load-bearing value for the cross-validator.

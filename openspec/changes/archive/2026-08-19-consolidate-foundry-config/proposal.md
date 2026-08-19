## Why

The service talks to exactly one Azure resource — the AI Foundry account `aif-eugo-swc`
(`kind: AIServices`) — but its configuration models that resource as two, under
`DocumentIntelligence:` and `AzureOpenAI:`, each with its own `ApiKey`. The resource exposes a
single key pair (`key1`/`key2`, two keys for rotation, not one per API), so those two settings were
always two names for one secret.

The duplication is not cosmetic. A developer naturally sets the key once, under whichever section
they were reading, and the other client silently falls through to `DefaultAzureCredential` — a
different authentication leg against the same resource. Nothing reports this: absence of a key is a
legal configuration, so the mismatch surfaces later as a startup-probe failure or a per-file
`engine_error` that names an *endpoint*, sending the operator to debug the network instead of the
config. This is the state of the repository's own development configuration today.

## What Changes

- **New `Foundry:` configuration section** modelling the resource as it actually is: one key, two
  API surfaces, one deployment alias.

  ```jsonc
  "Foundry": {
    "ApiKey": "",                          // key1 or key2 — one pair for the whole resource
    "DocumentIntelligenceEndpoint": "",    // https://<resource>.cognitiveservices.azure.com/
    "OpenAIEndpoint": "",                  // https://<resource>.openai.azure.com/
    "DeploymentNameVision": "model-eugo-docint-vision"
  }
  ```

- **BREAKING (configuration, not API):** the `DocumentIntelligence:` and `AzureOpenAI:`
  configuration sections are removed. `DocumentIntelligence:Endpoint`, `DocumentIntelligence:ApiKey`,
  `AzureOpenAI:Endpoint`, `AzureOpenAI:ApiKey`, and `AzureOpenAI:DeploymentNameVision` no longer bind.
- **The layout and vision clients authenticate identically**, both from `Foundry:ApiKey`. The
  key-or-`DefaultAzureCredential` fallback is retained, but it is now one decision for the resource
  rather than two independent ones that can disagree.
- **A leftover legacy key fails the service at boot**, naming the replacement. Silently ignoring a
  stale `DocumentIntelligence:ApiKey` would reintroduce exactly the failure this change removes, in
  a form that looks like a network fault.
- **BREAKING (chart values):** `azure.documentIntelligence.endpoint`, `azure.openAI.endpoint`, and
  `azure.openAI.deploymentNameVision` are replaced by a `foundry.*` block. Releases carrying the old
  keys must be updated; EuGo-infra owns release execution and is the coordination point.
- Two endpoints are retained deliberately. The resource advertises Document Intelligence only on
  `cognitiveservices.azure.com` (as `FormRecognizer`) and Azure OpenAI only on `openai.azure.com`;
  the hosts are not interchangeable and neither is derivable from the other.
- The boot-time effective-configuration log continues to name the key while never valuing it.

**The frozen `/v1` contract is untouched.** No request field, response field, `kind` value, error
code, or status code changes, and **EuGo-Web needs no coordination and no redeploy.**

## Capabilities

### New Capabilities
- `foundry-configuration`: how the service is configured to reach its Azure AI Foundry resource —
  the credential and endpoint surface an operator sets, what the service does when a value is
  absent, what it does when a retired value is present, and what it discloses about that
  configuration at boot.

### Modified Capabilities

None. This is the first capability recorded under `openspec/specs/`; no existing requirements change.

## Impact

**Configuration surface (operator-facing, breaking):** `src/DocInt.Api/appsettings.json`;
`charts/eugo-docint/values.yaml` and `templates/deployment.yaml`. Anyone holding user-secrets or an
untracked `appsettings.Development.json` must re-key them — the boot-time rejection makes that
loud rather than silent.

**Code:** `Configuration/DocIntOptions.cs` (the two option classes collapse into one, with their
binding and validation); `Engines/AzureLayoutAnalysisClient.cs`; `Engines/AzureVisionChatClient.cs`;
`Startup/DocumentIntelligenceStartupProbe.cs`; `Startup/AzureOpenAIStartupProbe.cs`;
`Telemetry/StartupConfigurationLog.cs` (the `Roots` allowlist is keyed on section names — without
`Foundry` the boot dump loses these keys entirely).

**Tests:** `OptionsTests`, `StartupConfigurationLoggingTests` (asserts on the literal
`AzureOpenAI:ApiKey`), `StartupConnectivityCheckTests`, `DependencyHealthTests`, `DocIntAppFactory`,
`LiveSmokeTests`.

**Docs:** `README.md` (the configuration reference table and the credentials guidance) and
`CLAUDE.md` (the live-smoke export block). The dated documents under `docs/superpowers/` mention the
old names and are **deliberately left stale** — they are historical records of shipped work.

**Not affected:** the `/v1` contract, the engine routing and per-file error model, CI (which sets no
credentials), and the pod security context and volume mounts.

**Siblings:** `aif-eugo-swc` also hosts `model-eugo-mcp-embedding` and `model-eugo-web-decision`, so
"Foundry" names a shared platform resource rather than a docint-local one. If EuGo-mcp or EuGo-web
later adopt a configuration section for it, the name is worth aligning across the three.

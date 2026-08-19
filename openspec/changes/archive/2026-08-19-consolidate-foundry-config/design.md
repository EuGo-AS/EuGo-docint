## Context

See `proposal.md` — *Why*. The facts that shape the approach were read off the resource itself
(management plane, so no VNet access was needed):

| Fact | Value |
| --- | --- |
| Resource | `aif-eugo-swc`, resource group `rg-eugo-foundry-swc`, Sweden Central |
| Kind | `AIServices` — one multi-service Foundry account, not two resources |
| Keys | `key1`, `key2` — **one pair for the whole account**; a rotation pair, not one key per API |
| Document Intelligence host | `aif-eugo-swc.cognitiveservices.azure.com` (advertised as `FormRecognizer`) |
| Azure OpenAI host | `aif-eugo-swc.openai.azure.com` (`OpenAI Language Model Instance API`) |
| Vision deployment | `model-eugo-docint-vision` → `gpt-5.4-mini`, a deployment **on this account** |
| Public network access | `Disabled` — data plane reachable only through the private endpoint |

The account also carries `model-eugo-mcp-embedding` and `model-eugo-web-decision`, so it is the
shared EuGo Foundry resource rather than a docint-local one.

The seams this change touches: two option classes bound to two configuration sections; two engine
clients and two startup probes that each independently choose between `AzureKeyCredential` and
`DefaultAzureCredential`; a boot-time configuration dump whose section allowlist is keyed on those
section names; and a Helm values block that renders the endpoints as environment variables.

## Goals / Non-Goals

**Goals:**

- Model the resource as one thing: one credential, two API surfaces, one deployment alias.
- Make the credential decision unrepresentable-if-inconsistent — there should be no configuration
  in which the layout path authenticates by key while the vision path falls back to managed
  identity.
- Fail a migration loudly, at boot, in the same channel as the service's other configuration
  errors — never by degrading to a different authentication path.
- Keep the two-endpoint, independently-optional shape, because it reflects the resource.

**Non-Goals:**

- Migrating to the account's unified `services.ai.azure.com` surface (see *Deliberately deferred*).
- Any change to `/v1`, to engine routing, or to the per-file error model.
- Any change to how credentials are *stored*. User-secrets locally and Workload Identity in-cluster
  stay exactly as they are; this change renames what is read, not where it comes from.
- Introducing a compatibility window in which both old and new keys bind.

## Decisions

### D1 — One options class, `FoundryOptions`, bound to `Foundry`

`DocumentIntelligenceOptions` and `AzureOpenAIOptions` collapse into a single class carrying
`ApiKey`, `DocumentIntelligenceEndpoint`, `OpenAIEndpoint`, and `DeploymentNameVision`. Both engine
clients and both startup probes take `IOptions<FoundryOptions>`.

*Alternative considered — keep two classes, both reading a shared key.* Rejected. It preserves the
shape that produced the drift and needs a per-surface override chain to justify itself, and that
chain would insure against a second Foundry resource that does not exist. Sharing one object is what
makes the "surfaces cannot disagree" requirement structural rather than a rule someone has to
remember.

*Consequence worth naming:* the vision client now depends on an object that also carries the layout
endpoint, and vice versa. That coupling is deliberate — it is the resource, and the coupling is the
point.

*Making "cannot disagree" structural.* One shared property is necessary but not sufficient: four
call sites still each write their own `string.IsNullOrWhiteSpace(ApiKey) ? managed identity : key`
ternary, and four copies of a rule can still drift. The credential choice moves into one small
factory that all four call, which makes the rule a single expression — and, unlike the ternaries,
directly unit-testable without a network call. Note that the two SDK families want different
credential types (`AzureKeyCredential` for Document Intelligence, `ApiKeyCredential` for the Azure
OpenAI probe), so the factory decides *which branch* and each call site adapts the result; the
decision is shared even where the type cannot be.

### D2 — Endpoints stay two values, and are not derived

`DocumentIntelligenceEndpoint` and `OpenAIEndpoint` remain separate, independently optional, and
each validated as an absolute URI.

*Alternative considered — one `Foundry:ResourceName` from which both hosts are built.* Rejected on
evidence, not taste: the account advertises Document Intelligence only on `cognitiveservices` and
OpenAI only on `openai`, so a single value cannot serve both, and string-building the hosts breaks
on custom subdomains, sovereign suffixes, and private-endpoint DNS. Two values that are each written
down are cheaper than one value plus the rules for expanding it.

### D3 — Retired keys are rejected through the existing options-validation channel

Presence of `DocumentIntelligence:Endpoint`, `DocumentIntelligence:ApiKey`, `AzureOpenAI:Endpoint`,
`AzureOpenAI:ApiKey`, or `AzureOpenAI:DeploymentNameVision` in **any** configuration source fails
the service at boot with a message naming the retired key and its replacement.

Implemented as an `IValidateOptions<FoundryOptions>` that reads `IConfiguration`, so it lands in the
same failure channel as every other configuration error in this service — one clean exception before
the service accepts a request, rather than a second bespoke failure mode.

*Alternative considered — an eager check in `AddDocIntOptions`.* Viable, and the codebase already
reads `builder.Configuration[...]` eagerly during registration in `AddStartupConnectivityCheck`. It
fails marginally earlier but in a different shape from the rest of the configuration errors.
Preferred only if the validation channel turns out not to run before the connectivity check; the
task list settles that with a test rather than an assumption.

*Alternative considered — folding the check into `StartupConnectivityCheck`.* Rejected: that check
is switchable off via `DocInt:StartupProbe:Enabled`, so a retired key would slip through in exactly
the configuration where it does the most damage.

Match on **exact key paths**, never substrings — a retired-key check that fires on an unrelated
environment variable is worse than no check.

**Fire on a value, not on presence.** The un-prefixed environment-variable provider folds the entire
process environment into `IConfiguration` (the reason `StartupConfigurationLog` needs a root
allowlist at all), and `DocIntAppFactory` deliberately sets the endpoint keys to `""` so the offline
suite cannot reach real Azure from a developer's ambient variables. A check that fired on presence
would therefore reject configurations that select nothing and change no behaviour. A blank retired
key is inert; a valued one is the hazard.

**Known consequence, accepted:** a developer whose shell still exports
`DocumentIntelligence__Endpoint` — which is exactly what the current `CLAUDE.md` live-smoke block
tells them to do — will find that **every test that builds the app fails**, not only the live ones,
until they re-export. That is the check working as designed, and the message names the replacement;
it is recorded here and in `tasks.md` so it is diagnosed in seconds rather than mistaken for a
regression.

### D4 — The boot dump's allowlist follows the rename; redaction needs no change

`StartupConfigurationLog.Roots` is an allowlist of top-level section names. `Foundry` replaces the
two retired names. Without this the boot dump silently loses these keys — and that dump is the only
signal an operator has that a key is present at all.

Redaction is already correct and **must not be touched**: `IsSecretKey` matches its markers against
the key's last segment, and `ApiKey` still ends in `key`. `Foundry:ApiKey` logs as `***redacted***`
with no change to `SecretMarkers`.

The retired names are removed rather than kept as a safety net: a configuration carrying them cannot
reach the logging stage, because D3 fails the boot first.

### D5 — Chart values gain a `foundry` block; still no key value

```yaml
foundry:
  documentIntelligenceEndpoint: ""   # → Foundry__DocumentIntelligenceEndpoint
  openAIEndpoint: ""                 # → Foundry__OpenAIEndpoint
  deploymentNameVision: ""           # → Foundry__DeploymentNameVision
```

The `azure.*` block is removed. There is deliberately **no `foundry.apiKey` value**, for the reason
the chart has never had one: a key routed through values would sit in plaintext in the release
manifest, and in-cluster the pod authenticates with Workload Identity. Each empty value still omits
its variable, preserving the degraded-mode path.

**Versioning, and a release-ordering constraint that CI enforces.** This is a values-breaking change,
so it cannot be a chart patch: chart `0.1.6` → `0.2.0`. The image version lives in no file —
`release.yml` derives it from the git tag (`${GITHUB_REF_NAME#v}`) — so nothing in the repository
"sets" it. But the chart job resolves `appVersion` by searching for an existing image tag matching
the chart's `major.minor` and **fails the release** when none exists:

```
APP_VERSION="$(git tag -l "v${CHART_MM}.*" | sort -V | tail -1)"
# ::error:: no image tag v${CHART_MM}.* exists to pair this chart with
```

So bumping `Chart.yaml` to `0.2.0` makes any `chart-v0.2.*` release fail until a **`v0.2.0` image tag
exists**. The image release must be cut first, or the two cut together. This is discoverable only in
CI, which is why it is written down here and carries its own task. `appVersion` stays CI-stamped and
is never hand-edited.

**Pod security context and volume mounts are not touched by this change** — no `securityContext`
change, no change to the writable `/tmp` mount. The change is env-var naming, which `helm lint` and
`helm template` do catch, so this one does not need the real-pod verification that a security-context
or mount change would.

### D6 — Probe and dependency-check registration follows the new key paths

`AddStartupConnectivityCheck` registers one probe and one periodic health check per **configured**
endpoint, reading the endpoint from configuration at registration time. Only the key paths it reads
change; the one-endpoint-one-probe-one-check invariant and the health-check service names
(`Document Intelligence`, `Azure OpenAI`) stay as they are. Those names describe the API surface
being dialled, which is still accurate — renaming them would churn `/health` output for no gain.

## Risks / Trade-offs

- **Every existing developer configuration breaks at once, including the one in this repository.**
  → That is the intended behaviour, and D3 makes it a boot failure naming the replacement rather
  than a silent auth switch. `README.md` and `CLAUDE.md` are updated in the same change so the fix
  is one search away.

- **A deployed release carrying `azure.*` values stops rendering those variables.** → Helm fails on
  unknown values only if the template references them, so the realistic failure is a pod that comes
  up with no endpoints and answers `engine_unconfigured` for every Azure-served kind. The chart
  minor bump is the signal; EuGo-infra owns release execution and must update values in step.

- **The retired-key check reads the whole configuration, which on this host includes the process
  environment.** → Exact-path matching only (D3). A substring match would fire on unrelated
  variables and turn a safety check into an outage.

- **The consolidation removes the ability to point the two surfaces at different resources.** →
  Accepted, and it is not a capability being lost so much as one that was never used: there has only
  ever been one resource, and one key. If a second is ever needed, adding a per-surface override is a
  smaller change than carrying the machinery for it now.

- **Coupling both clients to one options object means a change to either surface's configuration
  touches a type both depend on.** → Accepted; see D1. The alternative is the drift this change
  exists to remove.

## Migration Plan

1. Land the code and configuration change together — options class, both clients, both probes, the
   boot-dump allowlist, `appsettings.json`, and the retired-key rejection in one step. A split
   leaves a window where the service reads neither name.
2. Update `README.md`'s configuration reference and credentials guidance, and `CLAUDE.md`'s
   live-smoke export block, in the same change.
3. Re-key local configuration: `dotnet user-secrets` and any untracked
   `appsettings.Development.json`. The boot failure is the prompt; nothing is silent.
4. Chart values rename plus the version bump, verified with `helm lint` and
   `helm template ci charts/eugo-docint -f charts/eugo-docint/ci/test-values.yaml`.
5. Cut the image tag `v0.2.0` **before** any `chart-v0.2.*` tag, or cut them together. The chart
   release job refuses to package a chart whose `major.minor` has no paired image tag (D5).
6. Hand EuGo-infra the values rename for the release pipeline. The in-cluster credential path is
   unaffected — the pod authenticates with Workload Identity and never carried a key — so this is an
   endpoint-variable rename only. **This handoff is outside this repository** and is not something
   the apply phase can complete; it must be raised explicitly rather than closed silently.

**Rollback:** revert the commit and pin the previous chart version. There is no data migration and
nothing persisted, so rollback is a redeploy. A rolled-back service reads the retired names again,
which is why step 3's re-keying should not be discarded until the change has settled.

## Deliberately deferred

- **The unified `services.ai.azure.com` surface.** The account advertises "AI Foundry API" and
  "Azure AI Model Inference API" on a third host that would collapse the two endpoints into one. It
  is not a configuration change — it means moving the vision path onto a different client and API —
  so it belongs in its own change with its own live verification. Recorded here so the endpoint
  question is not re-derived: **as of this change, two endpoints is correct**, and the reason is D2,
  not inertia.

- **Aligning the section name across the siblings.** `aif-eugo-swc` also serves EuGo-mcp and
  EuGo-web. If either adopts a configuration section for it, `Foundry` is the name to match. Not
  pursued here because neither sibling was inspectable from this workspace and docint's own
  configuration should not wait on that.

- **A per-surface key override.** Considered and rejected in D1. Revisit only when a second resource
  actually exists.

- **Key Vault references or rotation automation.** Out of scope. Workload Identity is already the
  in-cluster answer, and this change does not alter where secrets come from.

- **Removing the retired-key check after a deprecation window.** Left permanent for now: it costs a
  handful of exact-match lookups at boot, and the failure it prevents is one that looks like a
  network fault. Worth revisiting once no configuration anywhere carries the old names.

- **A live-suite run against the consolidated key.** The offline suite proves binding, validation,
  and redaction; only a real call proves one key authenticates both hosts. That call needs the
  tailnet (`publicNetworkAccess: Disabled`), so it is a verification step in `tasks.md` rather than
  a design decision — and its outcome cannot change the design, since the account exposes only the
  one key pair.

## Open Questions

- Does EuGo-infra pin `azure.documentIntelligence.endpoint` / `azure.openAI.*` in a values file
  under its own repository, or are they supplied at release time? This changes who applies step 5,
  not what this change does — the rename and the version bump are the same either way.

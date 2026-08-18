> **This change belongs to EuGo-infra, and is staged here only because that repository is not
> checked out on this machine and no OpenSpec store is registered for it.** It describes work on
> `vm-eugo-vpn-swc` and the tailnet DNS configuration — nothing in EuGo-docint is edited by it.
> Move it to EuGo-infra when that repository is available; do not implement it from this repo.

## Why

The Tailscale subnet router `vm-eugo-vpn-swc` is the developer path into `vnet-eugo-hub-swc`, and
it is the only path for a workstation: every EuGo Azure resource of consequence is private-endpoint
only. It stopped resolving names, and the failure survived a repair attempt, so it is worth stating
exactly what is and is not broken (all verified 2026-08-18):

| Layer | State |
| --- | --- |
| Tunnel | Healthy — `RouteAll` and `CorpDNS` on, router online |
| Route `10.60.0.0/16` | Healthy — advertised as a primary route; TCP 443 reaches `10.60.5.4`, `.5`, `.6` |
| TLS at the private endpoints | Healthy — Microsoft cert, SANs `*.cognitiveservices.azure.com` / `*.openai.azure.com` |
| Private DNS zones | Healthy — linked to both `vnet-eugo-hub-swc` and `vnet-eugo-spoke-swc` |
| DNS service on the router | Healthy — `dnsmasq` answers, forwarding the **public** suffixes to `168.63.129.16`, and returns `10.60.5.4` for the Document Intelligence hostname |
| **Split DNS suffix coverage** | **Incomplete — five services are registered only in `privatelink.*` form** |
| **Reproducibility** | **Unverified — the working configuration is not known to survive a rebuild** |

The VM was rebuilt and came back with a new tailnet address (`100.111.128.91`, replacing
`100.77.69.84`), while tailnet Split DNS kept naming the old one — so every query went to a node
that no longer existed. That has since been corrected.

**A correction to an earlier reading of this incident**, recorded because it cost time and would
cost it again: the router was diagnosed as "running no resolver" on the strength of `google.com`
failing through it. That was a bad probe. `dnsmasq` here is a *split* resolver — it is configured
with per-suffix upstreams and no general one, so refusing a public name is correct behaviour, not a
fault. Probing it with a name it is meant to serve shows it working. Any future diagnosis of this
path must use an in-scope name.

Everything reachable only through a private endpoint is therefore unresolvable from a workstation —
not one service but all of them: the Foundry account, ACR, blob storage, Postgres, AKS and Key
Vault all have Split DNS entries pointing at the same router. The immediate casualty is that
EuGo-docint's live smoke suite cannot run, but that is a symptom, not the scope.

The deeper problem is that this was hand-built. A rebuild silently dropped it, and nothing detected
that until someone tried to use it. Restoring it by hand would leave the next rebuild to fail the
same way.

## What Changes

The resolver itself needs nothing — it already works. What remains are the two gaps that let this
break silently and stay broken, plus the coverage hole they masked:

- **Split DNS gains the missing public suffixes.** Five services are registered *only* in
  `privatelink.*` form, which never matches a client query, so ACR, blob storage, Cosmos, Search and
  the cluster API are unresolvable from a workstation regardless of the resolver's health.
  `dnsmasq` is already configured to serve them — only the client-side routing of those suffixes is
  missing, so this is one console change away.
- **The configuration is provisioned with the VM.** This is the actual defect behind the outage:
  the rebuild produced a router without the tailnet DNS wiring, and nothing noticed. The advertised
  routes are equally unprotected — correct today by accident of the current instance.
- **A check that the path works**, so the next silent loss is caught by something other than a
  developer's failing test run.

`design.md` records the implementation choice for the resolver. Since a working `dnsmasq`
configuration already exists, that section now serves to explain why it is kept rather than
replaced.

## Capabilities

### New Capabilities
- `developer-vnet-access`: what a workstation joined to the tailnet can resolve and reach inside
  the VNet, and how that survives a rebuild of the machine providing it.

### Modified Capabilities

None.

## Impact

**EuGo-infra:** the provisioning of `vm-eugo-vpn-swc` (`rg-eugo-net-swc`, Ubuntu 24.04 LTS arm64,
`Standard_B2pts_v2`, admin `eugoadmin`), and `docs/net.md` § *Developer access*, whose current
description no longer matches the deployed machine.

**Tailscale tailnet configuration:** the Split DNS nameserver entries, which live in the admin
console rather than in any repository — worth calling out, because it means part of this capability
cannot be version-controlled alongside the rest and has to be documented instead.

**EuGo-docint:** none directly. Its `CLAUDE.md` already documents the developer access path and
needs no change; its live smoke suite starts working again as a consequence. The blocked
verification task in the `consolidate-foundry-config` change closes once this ships.

**Not affected:** any Azure resource configuration. Nothing here changes a private endpoint, a DNS
zone, a VNet link, or a firewall rule — all of those were verified healthy. The gap is a service on
one VM plus one console setting.

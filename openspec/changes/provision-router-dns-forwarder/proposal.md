> **This change belongs to EuGo-infra, and is staged here only because that repository is not
> checked out on this machine and no OpenSpec store is registered for it.** It describes work on
> `vm-eugo-vpn-swc` and the tailnet DNS configuration — nothing in EuGo-docint is edited by it.
> Move it to EuGo-infra when that repository is available; do not implement it from this repo.

## Why

The Tailscale subnet router `vm-eugo-vpn-swc` is the developer path into `vnet-eugo-hub-swc`, and
it is the only path for a workstation: every EuGo Azure resource of consequence is private-endpoint
only. Names stopped resolving from a workstation, and two successive diagnoses of *why* were wrong,
so the table below states only what has been measured, and from where.

**Verified 2026-08-19, on the VM and from a workstation:**

| Layer | State |
| --- | --- |
| Tunnel | Healthy — router online, ICMP and TCP reach it |
| Route `10.60.0.0/16` | Healthy — advertised; TCP 443 reaches `10.60.5.4`, `.5`, `.6` |
| TLS at the private endpoints | Healthy — Microsoft cert, SANs `*.cognitiveservices.azure.com` / `*.openai.azure.com` |
| Private DNS zones | Healthy — linked to both `vnet-eugo-hub-swc` and `vnet-eugo-spoke-swc` |
| DNS service on the router | **Healthy** — `dnsmasq` answers on `100.111.128.91:53` with `10.60.5.4` and `10.60.5.5` for the two Foundry hosts |
| Provisioning of the router | **Healthy** — cloud-init installs `dnsmasq`, writes `/etc/dnsmasq.d/`, and runs `tailscale up --advertise-routes=10.60.0.0/16` |
| **Split DNS suffix coverage** | **Incomplete — five services are registered only in `privatelink.*` form** |
| **Auth-key handling** | **Exposed — the cloud-config carries a Tailscale auth key in plaintext** |
| **Detection** | **Absent — nothing notices when this path breaks** |

### The workstation failure was never the router

A workstation on this tailnet cannot resolve the private names, but the cause is local to the
workstation and outside this repository's reach: **a second VPN client intercepts every UDP port 53
query and answers it itself**, so queries never reach the router at all.

The proof does not depend on identifying the product. A UDP DNS query sent to `192.0.2.1` — a
TEST-NET address reserved by RFC 5737, which cannot host a resolver — returns a well-formed
NXDOMAIN with a forged source address. So do queries to `8.8.8.8`, to `1.1.1.1`, and to a tailnet
address with no peer behind it. Only the corporate resolver `10.21.32.51` answers truthfully.
Meanwhile `tcpdump` on the router's `tailscale0` captured **zero packets** while four queries were
fired at it, and TCP 53, TCP 22 and ICMP to that same address all succeed. UDP 53 is the only
affected path, and it is intercepted before it leaves the machine.

The workstation observed here runs Tailscale alongside a Palo Alto GlobalProtect client
(`PanGPS`, adapter `PANGP Virtual Ethernet Adapter Secure`). That is the likely enforcer — DNS
pinning to the corporate resolver is standard GlobalProtect behaviour — but it is an inference, not
a measurement: a `svchost` process bound to `0.0.0.0:53` is also present and unexplained. The
discriminating test is to disconnect GlobalProtect and re-probe `192.0.2.1`; if it stops replying,
the attribution is confirmed. Corporate VPN policy is not this project's to change, so the
practical consequence is a documented workaround, not a fix here.

### Two misreads, kept because they cost time

Both were reasonable and both were wrong; the pattern is worth more than either conclusion.

1. **"The router runs no resolver"** — concluded from `google.com` failing through it. A bad probe:
   `dnsmasq` here has per-suffix upstreams, so refusing an out-of-scope name proves nothing. Use an
   in-scope name.
2. **"The resolver runs but its config is gone"** — concluded from an in-scope name *also* returning
   NXDOMAIN through the router, reasoning that identical treatment of in-scope and public names
   means no per-suffix rules are loaded. The logic was sound; the input was fabricated. Both
   answers were forged locally and neither query ever reached the router.

The lesson generalises: **an answer that arrives is not evidence that it came from the server you
asked.** Before concluding anything about a remote resolver, confirm the query reaches it — with a
capture at the far end, or with the `192.0.2.1` control above. Every symptom here was equally
consistent with a healthy router, and it was.

### What is actually left

Everything reachable only through a private endpoint remains unresolvable from an affected
workstation — the Foundry account, ACR, blob storage, Postgres, AKS and Key Vault alike. But that
is the workstation's DNS interception, not the router. What remains genuinely open on the
infrastructure is narrower than this change first assumed: the five suffixes the tailnet never
routes to the router, an auth key sitting in plaintext, and the absence of any check that would
have caught either.

## What Changes

The resolver needs nothing, and neither does its provisioning — both were verified working on
2026-08-19. Three items remain, one of them new and more urgent than anything this change
originally set out to fix:

- **The auth key comes out of the cloud-config.** The VM's `runcmd` passes a Tailscale auth key as
  a plaintext literal, so it is readable by anyone who can reach the instance's user-data or run a
  command on the VM. It should be revoked and re-issued from a secret reference. This was found
  while verifying the provisioning and is unrelated to DNS, but it is the highest-severity item
  here.
- **Split DNS gains the missing public suffixes.** Five services — ACR, blob storage, Cosmos,
  Search and the cluster API — are registered *only* in `privatelink.*` form, which never matches a
  client query, so they are unresolvable from a workstation regardless of the resolver's health.
  `dnsmasq` already carries public-suffix `server=` lines for all five; only the client-side
  routing is missing, so this is one console change away.
- **A check that the path works**, reporting which half failed. This is the item the incident
  argues for most strongly: three separate diagnoses were attempted from the workstation, two of
  them wrong, because resolution failure and route failure are indistinguishable from that vantage
  point and a forged answer is indistinguishable from a real one.

**Withdrawn from this change:** "make the configuration survive a rebuild." The premise was that
the router had been hand-built and a rebuild silently dropped its DNS wiring. That is false — the
cloud-config installs `dnsmasq`, writes both `/etc/dnsmasq.d/` files with public-suffix upstreams,
and advertises `10.60.0.0/16` on join. The config files on disk predate the current boot, so they
survived it. Group 2 of `tasks.md` is therefore already satisfied; it is kept only so a reviewer
can confirm the same thing rather than wonder why it vanished.

`design.md` records the implementation choice for the resolver, and stands: the existing `dnsmasq`
configuration is kept rather than replaced.

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

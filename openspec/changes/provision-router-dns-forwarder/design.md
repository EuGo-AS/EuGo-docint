## Context

See `proposal.md` — *Why*, including the table of what was verified healthy. The short version: the
tunnel, the route, the private endpoints, the certificate and the private DNS zone links are all
fine. One thing is missing — something on the tailnet that answers DNS — and one thing was stale,
the Split DNS nameserver address.

Facts the design rests on, all read on 2026-08-18:

- Router: `vm-eugo-vpn-swc`, `rg-eugo-net-swc`, Ubuntu 24.04 LTS **arm64**, `Standard_B2pts_v2`,
  admin `eugoadmin`, in `vnet-eugo-hub-swc/snet-vpn`. Tailnet address `100.111.128.91`
  (was `100.77.69.84` before the rebuild).
- It advertises `10.60.0.0/16` as a primary route, and that route works.
- Foundry private endpoints: `10.60.5.4` (cognitiveservices), `10.60.5.5` (openai), `10.60.5.6`.
- Private DNS zones `privatelink.cognitiveservices.azure.com` and `privatelink.openai.azure.com`
  live in `rg-eugo-net-swc` and are linked to both the hub and spoke VNets.
- No Azure DNS Private Resolver is deployed in the subscription.
- Ubuntu 24.04 runs `systemd-resolved`, which binds `127.0.0.53` only — nothing listens on the
  tailnet interface, which is why the router answers nothing.

## Goals / Non-Goals

**Goals:**

- One mechanism that resolves every private suffix, not one fix per service.
- Reproduced by provisioning, so a rebuild cannot silently drop it again.
- Diagnosable: when it breaks, the check should say which half broke.

**Non-Goals:**

- Changing any Azure resource: no endpoint, zone, VNet link or firewall rule is touched.
- Giving the tailnet general DNS service. Only the private suffixes are routed to the VNet;
  everything else keeps resolving the way it already does.
- Replacing Tailscale, or changing how developers authenticate to the tailnet.
- Anything in EuGo-docint. Its live suite starts working as a consequence, not as a change.

## Decisions

### D1 — Keep the existing `dnsmasq`; do not replace it

**Superseded reasoning, kept deliberately.** This decision originally recommended replacing a
resolver on the VM with a route to Azure's, on the belief that no resolver existed. Inspection
showed one does, correctly configured: `bind-dynamic` on `tailscale0` and `lo`, with per-suffix
upstreams to `168.63.129.16` registered on the **public** suffixes — including a comment explaining
why the public form is the load-bearing one. It answers correctly:

```
dig aif-eugo-swc.cognitiveservices.azure.com @100.111.128.91
  → aif-eugo-swc.privatelink.cognitiveservices.azure.com.
  → 10.60.5.4
```

**Keep it.** It already covers more suffixes than Split DNS routes to it, its per-suffix design
keeps public resolution off the tunnel, and replacing working infrastructure to satisfy a
preference formed from a bad diagnosis would be its own defect. The two options below are retained
only so the alternative is not re-derived.

The one thing worth carrying over from the superseded reasoning: `dnsmasq` is a component that must
be reproduced on rebuild, and it was not. That is D3's problem, not a reason to remove it.

<details>
<summary>The two implementations considered when the resolver was believed absent</summary>

**Option A — advertise `168.63.129.16/32` and point Split DNS at it.** Azure's platform resolver
already answers correctly for every linked private zone; it is simply not reachable over the tunnel
today, because `168.63.129.16` is outside the advertised `10.60.0.0/16`. Adding it as a second
advertised route makes it reachable, and Tailscale subnet routers SNAT by default, so the query
arrives with the router's VNet source address — which is the condition that resolver imposes.

```bash
tailscale up --advertise-routes=10.60.0.0/16,168.63.129.16/32
# then: approve the route, and set Split DNS for the private suffixes to 168.63.129.16
```

**Option B — run `dnsmasq` on the router**, bound to the tailnet interface, forwarding to
`168.63.129.16`; Split DNS keeps naming the router's tailnet address.

```bash
# /etc/dnsmasq.d/tailscale.conf
interface=tailscale0
bind-interfaces        # without this, dnsmasq takes 0.0.0.0:53 and collides with systemd-resolved
server=168.63.129.16
no-resolv
```

Option A would have removed the coupling to the router's tailnet address, which is what went stale;
its cost is a dependency on subnet-route SNAT staying enabled. Option B is what is deployed, and
Option B is what the tailnet already needs, since the per-suffix upstreams keep public resolution
off the tunnel — behaviour Option A does not offer.

</details>

### D2 — Split DNS entries stay on the public suffixes

`cognitiveservices.azure.com`, not `privatelink.cognitiveservices.azure.com`. A client asks for the
public hostname; the `privatelink.*` form appears only later in the CNAME chain, inside the
resolver, so a Split DNS route registered on it never matches a client query and fails silently.

**This is not only a tidiness point — half the estate is registered the wrong way.** Of the 17
entries present on 2026-08-18, five carry a public suffix and will start working the moment a
resolver exists:

```
cognitiveservices.azure.com   openai.azure.com   services.ai.azure.com
postgres.database.azure.com   vault.azure.net
```

The rest exist **only** in `privatelink.*` form, and so match nothing a client ever asks for:

```
privatelink.azurecr.io                privatelink.blob.core.windows.net
privatelink.documents.azure.com       privatelink.search.windows.net
privatelink.swedencentral.azmk8s.io
```

Those five need public-suffix entries added — `azurecr.io`, `blob.core.windows.net`,
`documents.azure.com`, `search.windows.net`, `swedencentral.azmk8s.io` — or the container registry,
blob storage, Cosmos, Search and the cluster API stay unresolvable even after the resolver is
fixed, with exactly the symptom this change is meant to remove. The duplicate `privatelink.*`
entries for the working five are inert and are left alone (see *Deliberately deferred*).

### D3 — Provisioning owns the configuration, and that is the actual deliverable

Whichever option is taken, the route advertisement and the DNS setting belong in the VM's
provisioning definition — cloud-init, or whatever EuGo-infra uses to build this machine — not in an
SSH session. The advertised routes are equally at risk: they happen to be correct today, but
nothing in the repository guarantees they come back after a rebuild.

The Split DNS entries themselves cannot be version-controlled: they live in the Tailscale admin
console. That asymmetry is unavoidable and is the reason D4 exists.

### D4 — A check, because the console half cannot be provisioned

Since one half lives outside any repository, the path can break without any commit. A periodic
check resolves one private hostname and connects to it on 443, and reports which half failed —
resolution or route — because at the workstation the two look identical and their fixes are
different.

Where it runs is EuGo-infra's call. The requirement is only that a person attempting unrelated work
is not the detector, which is how this outage was found.

## Risks / Trade-offs

- **`168.63.129.16` is a platform address with unusual routing semantics; forwarding it over a
  subnet route is a legitimate but uncommon pattern.** → Verify from a workstation immediately after
  the change, not just from the VM: the VM can reach it either way, so testing there proves nothing
  about the tunnel path.

- **Option A's dependency on SNAT is invisible until it breaks.** → D1 records it; the provisioning
  definition should carry the same note next to the flag.

- **Repointing Split DNS fixes the symptom without fixing the cause.** → That already happened once
  in this investigation: the address was corrected, resolution still failed, and the second half was
  only found because the router was queried directly for an unrelated public hostname. Any repair
  should confirm with `google.com` through the nameserver before concluding the DNS half is done.

- **The tailnet address changes on rebuild.** → Option A removes this coupling entirely. Under
  Option B it remains, and the provisioning definition should either pin the address or document
  the console step as part of the rebuild.

## Deliberately deferred

- **Azure DNS Private Resolver.** The clean answer: an inbound endpoint at a `10.60.x.x` address,
  already reachable over the existing route, needing nothing on the VM and surviving its rebuild
  entirely. Not pursued because none is deployed and it costs roughly €180/month for one hostname
  lookup path. Revisit if the tailnet grows more consumers, or if the VM becomes a liability for
  reasons beyond DNS.

- **Removing the router from the developer path.** A jumpbox or a cluster-side runner would sidestep
  workstation DNS altogether. A larger change to how developers work, and orthogonal to fixing what
  is already deployed.

- **The stale `privatelink.*` Split DNS entries.** Harmless, and removing them is cleanup that would
  confuse this change's verification — if resolution starts working, it should be because the
  resolver works, not because entries were removed at the same time. Tidy separately.

## Open Questions

- Which machine runs the D4 check, and how it reports. This changes nothing about the fix itself
  and is EuGo-infra's to answer.

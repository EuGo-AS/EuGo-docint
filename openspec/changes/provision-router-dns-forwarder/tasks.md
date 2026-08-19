**Execute this in EuGo-infra, not in EuGo-docint.** Nothing here edits a file in this repository;
the change is staged here only because EuGo-infra is not checked out on this machine. There is no
`dotnet` gate to run — the verification at the end of each group is a resolution and connection test
from a **workstation**, since the router can reach things the tunnel cannot.

## 1. Complete the suffix coverage

The resolver already works — `dnsmasq` on the router answers correctly and is configured for more
suffixes than the tailnet routes to it. Nothing on the VM needs changing in this group.

- [ ] 1.1 Add the missing **public-suffix** Split DNS entries: `azurecr.io`,
  `blob.core.windows.net`, `documents.azure.com`, `search.windows.net`, `swedencentral.azmk8s.io`.
  Each is registered today only in `privatelink.*` form, which never matches a client query, so
  those five services are unresolvable from a workstation no matter how healthy the resolver is.
  `dnsmasq` already has upstreams for all of them.
- [ ] 1.2 Verify from a workstation that all five resolve to `10.60.x.x`, plus the three Foundry
  hostnames — using an **in-scope** name in every probe. A public name such as `google.com` is
  refused by design and proves nothing (design D1).
- [ ] 1.3 Leave the duplicate `privatelink.*` entries alone; tidying them is deferred and would
  confuse this verification.

## 2. Make it survive a rebuild

- [ ] 2.1 Move the configuration into the VM's provisioning definition: the advertised routes **and**
  the `dnsmasq` package and its `/etc/dnsmasq.d/` config. The routes are equally unprotected today —
  they are correct by accident of the current instance, not by definition.
- [ ] 2.2 Carry the existing config's own comment across: the per-suffix upstreams must be
  registered on the **public** suffixes, because the `privatelink.*` form is only ever an
  intermediate hop the resolver expands internally and matches nothing a client asks for.
- [ ] 2.3 Rebuild or reprovision the router from that definition and repeat 1.2 with no manual step
  in between. This is the requirement — an unrepeated rebuild leaves the actual defect unfixed, and
  the change should not be considered done without it.
- [ ] 2.4 Note in the provisioning definition that the Split DNS entries live in the Tailscale admin
  console and are not reproduced by a rebuild, including what they must be set to.

## 3. Detection

- [ ] 3.1 Add a check that resolves one private hostname and connects to it on 443, reporting which
  half failed — resolution or route — since they are indistinguishable at the workstation and have
  different fixes.
- [ ] 3.2 Make its result distinguish "verified working" from "not yet checked", so a silent
  absence of signal cannot read as health.
- [ ] 3.3 Prove it fails correctly: point Split DNS at an address that answers nothing and confirm
  the check reports the resolution half, then restore. A detector that has only ever been observed
  passing has not been tested.

## 4. Documentation and handoff

- [ ] 4.1 Update `docs/net.md` § *Developer access* to match what is actually deployed, including
  the rebuild behaviour and the console step.
- [ ] 4.2 Record that the stale `privatelink.*` Split DNS entries are inert and should be tidied
  separately, not as part of this change (design, *Deliberately deferred*).
- [ ] 4.3 Tell EuGo-docint that the path is restored, so the blocked live-smoke verification in its
  `consolidate-foundry-config` change can be run and closed.

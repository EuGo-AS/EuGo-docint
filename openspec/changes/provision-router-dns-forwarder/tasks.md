**Scope corrected 2026-08-19.** The workstation symptom that prompted this change — private names
not resolving — is **not** caused by anything here. A second VPN client on the workstation
intercepts every UDP 53 query and answers it locally, so queries never reach the router; the router
itself resolves correctly. See `proposal.md`. What remains below is real but narrower: the five
unrouted suffixes, the exposed auth key, and the missing detection.

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

## 2. Rebuild survival — already satisfied, confirm only

Verified 2026-08-19: the cloud-config installs `dnsmasq`, writes `/etc/dnsmasq.d/00-listen.conf`
and `/etc/dnsmasq.d/azure-privatelink.conf` (public-suffix `server=` lines for all ten suffixes),
and joins with `--advertise-routes=10.60.0.0/16`. The premise that this was hand-built was wrong.
Confirm rather than rebuild.

- [x] 2.1 Configuration lives in the VM's provisioning definition — routes **and** the `dnsmasq`
  package and its `/etc/dnsmasq.d/` config. Confirmed present in the cloud-config.
- [x] 2.2 The per-suffix upstreams are registered on the **public** suffixes. Confirmed: the
  config's own comment says so, and the lines are there.
- [x] 2.3 The config survived the 2026-08-19 boot — both files predate it and `dnsmasq` answered
  correctly afterwards. A from-scratch reprovision was **not** performed; if that stricter proof is
  wanted, it is the one part of this group still open.
- [ ] 2.4 Note in the provisioning definition that the Split DNS entries live in the Tailscale admin
  console and are not reproduced by a rebuild, including what they must be set to.

## 2b. Get the auth key out of the cloud-config

Found while confirming the above, unrelated to DNS, and the highest-severity item in this change.

- [ ] 2b.1 Revoke the Tailscale auth key currently embedded as a plaintext literal in the VM's
  `runcmd`. Treat it as compromised: anyone with instance user-data access or command execution on
  the VM can read it and join the tailnet with it.
- [ ] 2b.2 Re-provision using a secret reference rather than a literal, and confirm the key value
  no longer appears in `/var/lib/cloud/instance/cloud-config.txt`.
- [ ] 2b.3 Prefer an ephemeral, pre-approved, tagged key so a leaked value expires on its own.

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

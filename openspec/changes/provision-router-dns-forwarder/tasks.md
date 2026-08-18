**Execute this in EuGo-infra, not in EuGo-docint.** Nothing here edits a file in this repository;
the change is staged here only because EuGo-infra is not checked out on this machine. There is no
`dotnet` gate to run — the verification at the end of each group is a resolution and connection test
from a **workstation**, since the router can reach things the tunnel cannot.

## 1. Restore resolution

- [ ] 1.1 Confirm the fault before changing anything: from a joined workstation,
  `Resolve-DnsName google.com -Server <router-tailnet-ip>` returns no answer. This distinguishes
  "no resolver" from "resolver without the private zones", and the two have different fixes.
- [ ] 1.2 Apply the chosen implementation from design D1 — Option A (advertise `168.63.129.16/32`
  and point Split DNS at it) unless there is a reason to prefer B. Record which was applied and why.
- [ ] 1.3 For Option A: approve the new route in the Tailscale admin console. For Option B: confirm
  `dnsmasq` binds only `tailscale0` and has not taken `0.0.0.0:53` from `systemd-resolved`.
- [ ] 1.4 Point the Split DNS entries for the private suffixes at the chosen nameserver, on the
  **public** suffixes only (design D2).
- [ ] 1.5 Verify from a workstation, not from the router: the router path answers for an ordinary
  public hostname, both Foundry hostnames resolve to `10.60.5.4` / `10.60.5.5`, and TLS to each
  validates using the public hostname. Testing from the VM proves nothing about the tunnel.
- [ ] 1.6 Verify the other private suffixes resolve too — registry, blob, database, cluster, key
  vault. One mechanism, so one broken suffix means the fix is per-service and therefore wrong.

## 2. Make it survive a rebuild

- [ ] 2.1 Move the configuration into the VM's provisioning definition: the advertised routes **and**
  whichever DNS mechanism was chosen. The routes are equally unprotected today — they are correct by
  accident of the current instance, not by definition.
- [ ] 2.2 For Option A, record next to the flag that it depends on subnet-route SNAT remaining
  enabled; disabling it breaks resolution in a way that looks exactly like this outage (design D1).
- [ ] 2.3 Rebuild or reprovision the router from that definition and repeat 1.5 with no manual step
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

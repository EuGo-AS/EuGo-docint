## Purpose

Describes what a developer workstation joined to the tailnet can resolve and reach inside the EuGo
VNet, and the requirement that this survives a rebuild of the machine providing that access.

## ADDED Requirements

### Requirement: Private-endpoint hostnames resolve from a joined workstation

A workstation joined to the tailnet, with the subnet route accepted, SHALL resolve the public
hostname of every EuGo resource that is reachable only through a private endpoint to that
endpoint's private address. Resolution SHALL use the public hostname, because that is the name the
certificate is issued for and the name every SDK and tool asks for.

#### Scenario: Foundry account hostnames

- **WHEN** a joined workstation resolves the Document Intelligence hostname or the Azure OpenAI
  hostname of the Foundry account
- **THEN** each answers with the private address of its endpoint, in the VNet's address range
- **AND** a TLS connection to that address using the public hostname validates against the
  certificate presented

#### Scenario: The other private-endpoint services

- **WHEN** a joined workstation resolves a hostname belonging to any other EuGo service published
  only through a private endpoint — the container registry, blob storage, the database, the
  cluster API, or the key vault
- **THEN** it answers with that service's private address, by the same mechanism and with no
  per-service configuration on the workstation

#### Scenario: A name outside the private suffixes is unaffected

- **WHEN** a joined workstation resolves an ordinary public hostname
- **THEN** it resolves normally, and joining the tailnet has not made general name resolution
  depend on the VNet path

### Requirement: The resolver the tailnet is directed to must answer

The nameserver that tailnet DNS configuration directs private-suffix queries to SHALL be a live
address that answers DNS queries. Directing queries at an address that does not answer SHALL be
treated as a fault, whether the address belongs to no device or to a device running no resolver.

#### Scenario: Nameserver address does not exist

- **WHEN** the configured nameserver address is not a device in the tailnet
- **THEN** the configuration is faulty and private-suffix resolution fails for every workstation

#### Scenario: Nameserver device exists but serves no DNS

- **WHEN** the configured nameserver address belongs to a live device that does not answer DNS
- **THEN** the configuration is equally faulty
- **AND** the symptom is indistinguishable from the case above at the workstation, which is why
  correcting only the address is not sufficient

#### Scenario: Working nameserver

- **WHEN** the configured nameserver answers a query for an ordinary public hostname
- **THEN** it is serving DNS, and private-suffix resolution can be diagnosed separately from
  whether any resolver is running

### Requirement: Access survives a rebuild of the machine that provides it

The routing and name resolution the developer path depends on SHALL be reproduced automatically
when the machine providing them is rebuilt or replaced. No manual step SHALL be required to return
a rebuilt machine to a working state.

#### Scenario: Machine is rebuilt

- **WHEN** the machine providing the developer path is rebuilt from its provisioning definition
- **THEN** it advertises the VNet route and answers DNS for the private suffixes without anyone
  connecting to it afterwards

#### Scenario: Machine rejoins under a new tailnet address

- **WHEN** a rebuilt machine joins the tailnet and receives a different address from the one it had
- **THEN** the change is reflected wherever that address is named, so queries are not left pointing
  at the previous address

### Requirement: Loss of the developer path is detectable without a person discovering it

Whether the path resolves and connects SHALL be observable by a check that does not depend on
someone attempting unrelated work. A silent loss that is only noticed when a developer's test run
fails is not acceptable.

#### Scenario: Path is broken

- **WHEN** private-suffix resolution stops working
- **THEN** a check reports it, naming which of the two halves failed — route or resolution — since
  the two have the same symptom at the workstation and different fixes

#### Scenario: Path is healthy

- **WHEN** both halves work
- **THEN** the same check passes, and its result distinguishes "verified working" from "not yet
  checked"

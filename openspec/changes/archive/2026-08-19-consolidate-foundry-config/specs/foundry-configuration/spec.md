## Purpose

Defines the credential and endpoint surface an operator sets so the service can reach its Azure AI
Foundry resource, what the service does when a value is absent or retired, and what it discloses
about that configuration at boot.

## ADDED Requirements

### Requirement: One credential for the whole Foundry resource

The service SHALL accept a single API key for the Foundry resource and use it for every Azure API
surface it calls. It SHALL NOT accept a separate key per surface, and the choice between key
authentication and the ambient managed identity SHALL be one decision for the resource rather than
an independent decision per surface.

#### Scenario: Key supplied

- **WHEN** the Foundry API key is configured and both endpoints are set
- **THEN** requests to both the document-layout surface and the image-description surface are
  authenticated with that key

#### Scenario: Key omitted

- **WHEN** no Foundry API key is configured and an endpoint is set
- **THEN** calls to that endpoint authenticate with the ambient managed identity
- **AND** the service starts normally, because an absent key is a supported configuration

#### Scenario: The authentication choice is made once

- **WHEN** the credential for either surface is resolved from a configuration whose key is blank or
  absent
- **THEN** the managed-identity branch is chosen
- **AND** the same resolution, given the same configuration, yields the same branch for both
  surfaces, because the choice is made from one value in one place

### Requirement: One endpoint per API surface

The service SHALL accept a separate endpoint value for the document-layout surface and for the
image-description surface, because the resource exposes them on different hosts and neither host is
derivable from the other. Each endpoint SHALL be independently optional. A configured endpoint MUST
be an absolute URI.

#### Scenario: Both endpoints configured

- **WHEN** both endpoints are set to absolute URIs
- **THEN** the service starts and every supported file kind is served by its engine

#### Scenario: One endpoint left blank — per-file failure inside a 200

- **WHEN** the document-layout endpoint is set, the image-description endpoint is blank, and a
  request to `/v1/extract` carries one PDF and one JPG
- **THEN** the response status is 200
- **AND** the PDF entry carries its extracted Markdown
- **AND** the JPG entry carries an `engine_unconfigured` error, leaving the rest of the response
  unaffected

#### Scenario: No endpoint configured

- **WHEN** both endpoints are blank
- **THEN** the service starts
- **AND** file kinds requiring Azure answer with an `engine_unconfigured` error per file, while
  spreadsheet extraction, which calls no Azure surface, succeeds normally

#### Scenario: Endpoint is not an absolute URI

- **WHEN** an endpoint is set to a value that is not an absolute URI
- **THEN** the service refuses to start and reports which endpoint value was rejected

### Requirement: The vision deployment alias is part of the resource configuration

The service SHALL take the name of the image-description model deployment alongside the resource's
key and endpoints. That name SHALL be required whenever the image-description endpoint is set, and
the service SHALL treat it as a deployment alias rather than a model identity, so the model behind
it can change without a configuration change here.

#### Scenario: Alias missing while the surface is enabled

- **WHEN** the image-description endpoint is set and the deployment name is blank
- **THEN** the service refuses to start and reports the missing deployment name

#### Scenario: Alias unused while the surface is disabled

- **WHEN** the image-description endpoint is blank and the deployment name is blank
- **THEN** the service starts, because no image request can be served and the name is not needed

### Requirement: Retired configuration keys fail the service at boot

The service SHALL refuse to start when a retired credential or endpoint key from the superseded
per-surface configuration **carries a value**, and the failure message SHALL name both the retired
key and its replacement. Ignoring such a key would leave the service running with an unintended
authentication path, which surfaces later as an endpoint or network failure rather than as the
configuration error it is.

A retired key present but blank SHALL NOT fail start-up: a blank value selects nothing and changes
no behaviour, and the process environment is folded into configuration wholesale, so failing on
presence alone would reject configurations that are in fact harmless.

#### Scenario: Retired key left behind after migration

- **WHEN** a retired per-surface API key carries a value in any configuration source
- **THEN** the service refuses to start
- **AND** the message names the retired key and the Foundry key that replaces it

#### Scenario: Retired endpoint left behind after migration

- **WHEN** a retired per-surface endpoint or deployment-name key carries a value in any
  configuration source
- **THEN** the service refuses to start
- **AND** the message names the retired key and its replacement

#### Scenario: Retired key present but blank

- **WHEN** a retired key is present with an empty value and no other retired key carries a value
- **THEN** start-up proceeds

#### Scenario: Clean configuration

- **WHEN** no retired key is present
- **THEN** the retired-key check reports nothing and start-up proceeds

### Requirement: Configuration is disclosed at boot without exposing the key

The service SHALL record its effective Foundry configuration once at start-up, so a running
instance's log shows the endpoints and deployment alias it actually resolved and whether a key is
present. The key's value SHALL never be recorded, in any configuration source.

#### Scenario: Key present

- **WHEN** the service starts with a Foundry API key configured
- **THEN** the start-up log contains an entry naming the key with its value redacted
- **AND** the key's value appears nowhere in the log

#### Scenario: Key supplied through the environment

- **WHEN** the key is supplied by an environment variable rather than a configuration file
- **THEN** the start-up log still names the key with its value redacted

#### Scenario: Endpoints and alias

- **WHEN** the service starts with endpoints and a deployment alias configured
- **THEN** the start-up log records each of those values as resolved, since none of them is a
  credential

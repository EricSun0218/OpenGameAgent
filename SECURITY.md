# Security policy

## Report a vulnerability

Do not open a public issue. Use GitHub's private [Report a vulnerability](https://github.com/EricSun0218/OpenGameAgent/security/advisories/new) form. Do not include a live credential or real player data.

The maintainer will acknowledge a report within three business days and provide an initial assessment within seven calendar days. Please allow time for investigation and a coordinated fix before disclosure.

## Credentials

A permanent provider key embedded in a shipped game client can be extracted. Running the runtime inside an engine does not protect it.

- For local development or BYOK, obtain the key at request time from appropriate protected storage.
- For officially funded inference, use a game server or agent service with quotas and revocation.
- Never place credentials in source, scenes, resources, logs, journals, crash reports, or runtime events.
- Set `OpenGameAgent:ServerApiKey` before exposing the included run endpoints beyond a trusted local network, and add TLS, player authentication, rate limits, and abuse controls at the gateway.

## Model and tool boundary

Model output and imported skill instructions are untrusted. JSON Schema validation improves structure but does not implement game rules. Game handlers must check identity, visibility, permissions, resources, revisions, limits, and legality before committing.

Do not expose unrestricted code execution, filesystem, shell, network proxy, reflection, or asset mutation tools to untrusted content.

A timed-out or canceled mutation may already have committed. Persist a stable operation ID and reconcile the outcome; never blindly replay a non-idempotent action.

## Data

Prompts, context, tool payloads, memory, generated media, and transcripts may contain player data. Define retention, deletion, consent, access control, and regional policy for the product. Included local-file stores are not encrypted.

Generated or remote resources require origin, type, size, checksum, quota, policy, and decoder-safety validation before import.

See [Deployment and security](docs/deployment-and-security.md) for integration guidance.

## Supported versions

| Version | Security support |
| --- | --- |
| Latest published `0.x` prerelease | Supported |
| Older `0.x` prereleases | Not supported |
| Unreleased `main` | Investigated when reproducible |

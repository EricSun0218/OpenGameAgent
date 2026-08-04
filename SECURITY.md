# Security policy

## Reporting a vulnerability

Do not open a public issue for a suspected vulnerability. Use GitHub's private
**Report a vulnerability** form in the repository Security tab:

https://github.com/EricSun0218/OpenGameAgent/security/advisories/new

Include affected versions, a minimal reproduction, expected impact, whether a
provider credential or player data is involved, and any proposed mitigation.
Do not include real player data or a live credential. Maintainers will
acknowledge a report within three business days and provide an initial
assessment within seven calendar days. Until resolution or coordinated
disclosure, maintainers will provide an update at least every fourteen calendar
days when there is material progress or a changed timeline.

Please allow maintainers a reasonable opportunity to investigate and release a
fix before public disclosure. The reporter and maintainers will coordinate the
disclosure date based on exploitability, affected releases, mitigation
availability, and downstream update time. Maintainers will credit reporters who
want attribution.

## Credential boundary

A permanent provider credential embedded in a shipped game client can be
extracted. Engine process isolation does not change that.

- For local development or BYOK, load credentials at request time from
  platform-protected storage or a process environment variable.
- For a consumer game that pays for inference, use a server relay or short-lived
  scoped credentials with quotas and revocation.
- Never place keys in source, scenes, resources, logs, journals, crash reports,
  or runtime events.

The default HTTP transport requires HTTPS for remote endpoints, disables
automatic redirects, rejects multiline bearer tokens, and does not include
provider error bodies in exceptions. A custom `IStreamingHttpTransport` is a
security boundary: it must preserve redirect rejection and must not forward a
credential, prompt, tool schema, or game context to a different origin.

## Model and tool boundary

Model output is untrusted:

- arguments are validated before dispatch;
- conflict keys are derived from catalog-owned templates;
- game rules are checked by the game host;
- write actions are journaled before dispatch;
- an uncertain result enters reconciliation and is not retried blindly;
- provider streams are fenced so stale retries cannot publish a final result.

Do not expose an unrestricted code execution, filesystem, shell, network, or
asset mutation tool to untrusted game content.

The complete reusable trust-boundary assessment is in
[docs/security-model.md](docs/security-model.md). Integrators must extend it for
their game-specific tools, economy, multiplayer authority, and player data.

## Data handling

Prompt and tool payloads may contain player data. Applications should define
retention, redaction, consent, and deletion policies appropriate to their
jurisdiction and distribution platform. The local journal is not encrypted by
the runtime; place it in an access-controlled location or provide an encrypted
`IDurableSessionStore`.

## Supported versions

| Version | Security support |
| --- | --- |
| Latest published `0.x` prerelease | Supported |
| Older `0.x` prereleases | Not supported |
| Unreleased commits | Investigated when reproducible on `main` |

After `1.0`, the project will publish a stable support window before changing
this policy. A security fix may require upgrading to the latest release when a
safe backport is not available.

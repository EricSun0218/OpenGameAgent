# Security policy

## Reporting a vulnerability

Do not open a public issue for a suspected vulnerability. During the private
Alpha, invited testers should use the private channel through which they
received access. Before public contributions open, maintainers must enable
GitHub private vulnerability reporting. Include affected versions,
reproduction steps, impact, and any proposed mitigation.

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

## Data handling

Prompt and tool payloads may contain player data. Applications should define
retention, redaction, consent, and deletion policies appropriate to their
jurisdiction and distribution platform. The local journal is not encrypted by
the runtime; place it in an access-controlled location or provide an encrypted
`IDurableSessionStore`.

## Supported versions

Security fixes are provided for the latest Alpha release only until a stable
support policy is published.

# Governance

OpenGameAgent uses a maintainer-led governance model. Maintainers are
responsible for the product boundary, public API, protocol compatibility,
security posture, release integrity, and repository administration.

## Decisions

Small changes are decided through pull-request review. Significant public API,
wire protocol, persistence, security-boundary, engine-support, or governance
changes begin with a public issue so alternatives and migration costs can be
reviewed before implementation.

Decisions favor, in order:

1. game-state correctness and prevention of duplicate side effects;
2. bounded resource use, recoverability, and explicit trust boundaries;
3. verified Godot and Unity integration behavior;
4. a coherent reusable Runtime instead of game-specific policy;
5. contributor and integrator usability.

Maintainers seek rough consensus but may make the final decision when consensus
cannot be reached. The decision and its technical rationale must remain visible
in the issue or pull request.

## Maintainers

Current maintainers and their responsibilities are listed in
[MAINTAINERS.md](MAINTAINERS.md). A new maintainer must demonstrate sustained,
constructive contributions, sound judgment at the game/Runtime authority
boundary, and reliable security handling. Existing maintainers approve changes
to the maintainer list.

Maintainers disclose material conflicts of interest and do not use private
security or conduct reports for competitive advantage. A maintainer who is the
subject of a conduct report must not adjudicate that report.

## Releases

Maintainers publish uniquely versioned releases only from a commit that passes
the documented source, test, engine, package, privacy, and provenance gates.
Release notes identify user-visible behavior, breaking changes, migrations,
and security fixes. Versioning and compatibility policy are documented in the
repository and may evolve before `1.0` with an explicit migration path.

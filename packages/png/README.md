# `@opengameagent/png`

Small, bounded PNG/APNG envelope inspection for OpenGameAgent packages. `inspectPng` validates the complete signature,
chunk stream, CRCs, critical PNG structure, image header, and APNG control sequence without decoding pixels or retaining
chunk payload copies. Invalid APNG ancillary metadata is reported as an invalid animation so callers can deliberately
fall back to the still PNG image; malformed base PNG structure remains fatal.

The host must provide the complete encoded-byte budget. The package supplies no workload default, performs no file or
network I/O, and does not expose a second image codec or media runtime.

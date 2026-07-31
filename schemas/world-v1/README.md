# Native world v1 schemas

This directory contains JSON Schema Draft 2020-12 descriptions for the seven
files consumed by `NativeWorldPackageCompiler`:

| Authored file | Schema |
| --- | --- |
| `world.json` | `world.schema.json` |
| `clocks.json` | `clocks.schema.json` |
| `numerics.json` | `numerics.schema.json` |
| `events.json` | `events.schema.json` |
| `interactions.json` | `interactions.schema.json` |
| `agents.json` | `agents.schema.json` |
| `knowledge.json` | `knowledge.schema.json` |

`native-world-common.schema.json` holds shared definitions. Keep it beside the
seven file schemas when resolving relative `$ref` values.

The schemas describe the compiler's closed file shapes, supported declarative
conditions and effects, canonical integer strings, number-free authoritative
values, and the supported interaction-parameter schema subset. Compilation is
still authoritative for rules JSON Schema cannot express portably, including
UTF-8 byte limits, exact Int64 range, configured collection limits, duplicate
JSON properties, cross-file references, unique compound identifiers, numeric
minimum/maximum binding, target-count ordering, aggregate resource budgets, and
condition depth/node budgets.

The reusable `fixtures/world-v1/interactive-smoke` package is valid against
these schemas and is also compiled and executed by the portable world tests.

# Third-party notices

The MIT license at the repository root covers OpenGameAgent's original code. Third-party components retain their own
copyright and license terms. Dependency source and binaries are installed separately; `node_modules`, model weights,
and engine editor distributions are not included in this source snapshot.

## Generated code

- The HTTP client under `packages/client/src/generated` is generated with `@hey-api/openapi-ts` from the project's
  OpenAPI document. Copyright (c) Hey API. The [MIT notice](docs/licenses/hey-api-MIT.txt) is also included in the client
  package's `THIRD_PARTY_NOTICES.md`.
- Engine protocol DTOs are generated with [quicktype](https://github.com/glideapps/quicktype) (`quicktype-core`), licensed
  under [Apache-2.0](docs/licenses/quicktype-Apache-2.0.txt). Each engine integration includes a third-party notice.

The generated output is specialized for OpenGameAgent's protocol by the checked-in generator scripts.

## Installed dependencies

Exact package versions, integrity hashes, and declared licenses are recorded in these lockfiles:

- [Workspace dependencies](package-lock.json)
- [OpenAPI generator dependencies](tools/openapi-generator/package-lock.json)
- [Engine DTO generator dependencies](tools/engine-dto-generator/package-lock.json)

Runtime dependencies include the MIT-licensed Pi packages (`@earendil-works/pi-*`), the Model Context Protocol SDK,
AI SDK, SQLite bindings, and other packages listed in each workspace manifest. These declarations do not relicense
their transitive dependencies.

In particular, the image stack includes Apache-2.0-licensed `sharp` and platform binaries containing LGPL-3.0-or-later
libvips components. The dependency tree also includes MPL-2.0-licensed `lightningcss`, BlueOak-1.0.0-licensed `lru-cache`,
and other permissive licenses. Keep the installed components' license and notice files when redistributing them, and
meet the source, modification, and relinking requirements that apply to the particular binaries you distribute.

Optional host-supplied models, game assets, external services, and engine software have separate terms. They are not
licensed by OpenGameAgent's MIT grant.

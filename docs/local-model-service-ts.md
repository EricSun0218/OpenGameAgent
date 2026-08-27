# Local model services

OpenGameAgent uses the same agent loop for local and cloud models. `@opengameagent/kernel-pi` provides trusted model profiles for Ollama, LM Studio, LocalAI, llama.cpp, and vLLM, plus an optional lifecycle layer for local services.

`LocalGameModelCatalogClient` discovers a bounded model catalog from a fixed loopback endpoint. It understands OpenAI-compatible `/v1/models`, Ollama `/api/tags`, and llama.cpp router state. The model cannot choose or alter the endpoint.

```ts
import { LocalGameModelCatalogClient, createLocalGameModelPreset } from "@opengameagent/kernel-pi";

const catalog = new LocalGameModelCatalogClient({ backend: "ollama" });
const models = await catalog.list();

const preset = createLocalGameModelPreset({
  backend: "ollama",
  model: models[0]!.id,
  input: ["text", "image"],
});
```

`LocalGameModelService` first probes an already running service. If it is unavailable, the host may supply a `LocalGameModelProcessController`; OGA starts it once, waits for the same bounded catalog, and records whether it owns that process. `SpawnedGameLocalModelProcess` is the included exact-executable implementation. It never uses a shell, hides Windows process windows, and discards child output so model-service logs cannot become agent diagnostics.

```ts
import {
  LocalGameModelService,
  SpawnedGameLocalModelProcess,
} from "@opengameagent/kernel-pi";

const service = new LocalGameModelService({
  backend: "llama.cpp",
  process: new SpawnedGameLocalModelProcess({
    executable: "D:/models/llama-server.exe",
    arguments: ["--host", "127.0.0.1", "--port", "8080"],
  }),
});

const ready = await service.start();
```

The framework does not bundle or automatically download model weights. Games decide which models they distribute or ask players to install. Discovery responses, model counts, timeouts, arguments, and endpoints are bounded. Remote endpoints are rejected; use the normal trusted provider registry for cloud models.

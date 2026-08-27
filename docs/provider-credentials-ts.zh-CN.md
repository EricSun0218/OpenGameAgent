# Provider 凭据

`@opengameagent/credentials-keyring` 是 OGA Provider 凭据边界到操作系统凭据存储的可选适配器。在 Windows 上，原生 Keyring 会使用当前用户的 Windows 凭据服务。包内不包含任何 Provider Key，也不会把 Secret 复制到异常、transcript、事件、trace 或模型请求元数据中。

```ts
import { KeyringGameProviderCredentialStore } from "@opengameagent/credentials-keyring";
import { createPiGameModelRegistry } from "@opengameagent/kernel-pi";

const credentials = new KeyringGameProviderCredentialStore({ service: "MyGame.AI" });
await credentials.set("deepseek", { key: playerProvidedKey });

const models = createPiGameModelRegistry({
  credentials,
  profiles,
});
```

凭据按宿主定义的 service 和 provider ID 隔离，使用版本化 envelope 和 revision 检查。删除凭据时会用不含 Secret 的 revision 墓碑覆盖原值，后续 compare-and-set 不会因版本归零产生 ABA 问题。同一个 store 实例会按 Provider 串行化并发访问。损坏或不可用的系统凭据会 fail closed，不会降级保存明文。

这能防止普通文件泄露直接暴露静态凭据，但不能向控制同一台机器和进程的玩家隐藏永久开发者 Key。客户端游戏应使用玩家自带 Key、本地模型、开发者签发的短期 Token 或可信游戏服务。服务端部署可以换成自己的 Secret Manager，同时继续使用同一个 `GameProviderCredentialSource` 边界。

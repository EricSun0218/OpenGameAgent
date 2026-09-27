# @opengameagent/extension-kit

Developer tooling for statically linked OpenGameAgent extensions. It gives a game team one strict manifest, deterministic
permission and dependency diagnostics, a bounded registration seam, a real-runtime conformance runner, a buildable
TypeScript scaffold, and an editor-only stop-the-world reload coordinator.

This package is for developers extending their own game. It is not a player-facing character-card importer, a package
marketplace, or an executable-code loader. Extension modules are normal dependencies selected and constructed by the
game host. Pi remains the only Agent and Tool loop.

## Manifest and permissions

`extension.json` is host/development input and is never inserted into model context:

```json
{
	"schemaVersion": "1",
	"id": "studio.world-context",
	"version": "1.0.0",
	"permissions": ["context.contribute"],
	"dependencies": [{ "id": "studio.shared", "versionRange": "^2.0.0" }]
}
```

`parseGameExtensionManifest()` requires the host to provide every byte, depth, identifier, version-range, and dependency
budget. It uses duplicate-key-safe strict JSON parsing and npm's maintained `node-semver` implementation. Unknown fields,
unknown or repeated permissions, self-dependencies, repeated dependencies, non-canonical versions, and invalid ranges fail
closed.

The closed permission vocabulary is deliberately small:

- `context.contribute` registers a `GameContextSource`.
- `request.contribute` registers a `GameRequestSource`.
- `tools.contribute` registers a `GameToolSource`.

It does not grant game-state write authority. Contributed Tools still pass through the host's ordinary visibility policy,
argument guard, reliable Action boundary, and authoritative receipt handling.

`diagnoseGameExtensionSet()` validates host grants, exact identities, strict dependency ranges, duplicates, missing
dependencies, and dependency cycles. A passing report contains a deterministic dependency-first order. Diagnostics are
never silently truncated: exceeding the host's diagnostic budget fails the operation.

## Statically linked modules and conformance

A module configures only through the bounded registration seam:

```ts
import type { GameExtensionModule } from "@opengameagent/extension-kit";

export const extension: GameExtensionModule = {
	descriptor: { id: "studio.world-context", version: "1.0.0" },
	configure(api) {
		api.registerContextSource(worldContextSource);
		return async () => closeWorldContextSource();
	},
};
```

`activateGameExtensions()` checks the complete set before calling any module, configures it in dependency order, closes
each registration API immediately after configuration, composes resources onto host-owned actor options, and cleans up in
reverse order. It captures callbacks and rejects duplicate resource identities. It does not discover or import modules.

`runGameExtensionConformance()` activates the same module through the same seam and performs one real request through
`GameRuntime` and the Pi-backed `GameAgent` using a deterministic fake model. Context, request, and Tool sources therefore
cross their real production integration points. The caller supplies the runtime, context, Tool, input, activation, and
diagnostic budgets; the kit does not invent production defaults.

## Scaffold

`renderGameExtensionScaffold()` returns a fixed, detached file set containing `extension.json`, a typed module, package
metadata, TypeScript configuration, and a README. `writeGameExtensionScaffold()` validates everything before disk I/O,
claims the destination with an exclusive directory creation, and never overwrites an existing file, directory, or symlink.
An incomplete marker is present until every file has been written and finalization succeeds; ordinary failure or cancellation removes
only paths created by the operation. A leftover marker identifies a process-crash interruption for manual recovery.

## Editor-only reload boundary

`EditorGameExtensionReloader` coordinates complete host generations; it does not reload code itself. For each replacement
it serially:

1. asks the current generation to stop and drain gameplay work;
2. disposes that generation;
3. calls the host factory for the replacement; and
4. publishes only the validated replacement generation.

Concurrent requests are serialized. A late candidate after cancellation is disposed, lifecycle faults prevent a competing
generation from being constructed, and close is terminal. Production servers and active gameplay must construct their
runtime normally rather than using this editor-only class.

---

# 中文说明

`@opengameagent/extension-kit` 面向扩展自己游戏的开发团队，提供严格扩展清单、权限与依赖诊断、受限资源注册、
真实运行时一致性测试、可编译 TypeScript 脚手架，以及仅供编辑器使用的整代重载协调器。

它不是玩家导入人设的入口，不支持角色卡、PNG 元数据或酒馆格式，也不是插件市场和任意代码加载器。扩展模块必须
由游戏工程以普通依赖静态选择并构造；Pi 仍然是唯一的 Agent 与 Tool 循环。

## 清单、权限与依赖

`extension.json` 只供宿主和开发工具读取，绝不会进入模型上下文。`parseGameExtensionManifest()` 要求宿主显式提供
字节、JSON 深度、标识符、版本范围和依赖数量预算；重复 JSON 键、未知字段、未知或重复权限、自依赖、重复依赖、
非规范 SemVer 与无效版本范围都会失败关闭。

权限词表只有三项：

- `context.contribute`：注册 `GameContextSource`；
- `request.contribute`：注册 `GameRequestSource`；
- `tools.contribute`：注册 `GameToolSource`。

这些权限不代表游戏状态写权限。扩展贡献的 Tool 仍须经过宿主原有的可见性策略、参数守卫、可靠 Action 边界与权威
回执处理。`diagnoseGameExtensionSet()` 会检查宿主授权、依赖版本、缺失项、重复项和依赖环；只有全部通过才返回稳定的
“依赖优先”顺序，诊断也不会因容量不足而静默截断。

## 激活与一致性测试

`activateGameExtensions()` 在调用任何模块前先验证完整集合，按依赖顺序配置，配置返回后立即关闭注册入口，将资源组合
到宿主持有的 actor options，并按相反顺序释放。它只接收宿主已经导入的模块，不负责发现、下载或执行包内代码。

`runGameExtensionConformance()` 使用相同注册接缝，在真正的 `GameRuntime` 和 Pi-backed `GameAgent` 上运行一次确定性的
假模型请求。因此 context、request 与 Tool source 都会经过真实生产集成点。运行时、上下文、Tool、输入和诊断预算均由
调用方提供，本包不设置生产默认值。

## 脚手架与编辑器重载

`renderGameExtensionScaffold()` 生成固定、可分离的文件集合；`writeGameExtensionScaffold()` 在任何磁盘写入前完成验证，
再用独占目录创建占用目标，因此绝不覆盖现有文件、目录或符号链接。全部文件写完前会保留未完成标记；普通失败或取消只清理
本次操作创建的路径。若进程崩溃，残留标记会明确指出目标需要人工恢复。

`EditorGameExtensionReloader` 只协调完整宿主代次：先停止并排空当前代，再释放当前代，最后才调用宿主工厂构造新代。
并发请求会串行化；取消后迟到的新代会被释放；生命周期故障会阻止并行替换。它本身不加载或卸载代码，生产服务器和活跃
游戏流程不应使用此编辑器专用类。

# OpenGameAgent Runtime Protocol v1

OpenGameAgent Runtime Protocol 是 OpenGameAgent 面向游戏引擎、原生客户端、sidecar 和自托管服务的可选版本化边界。它不属于 `OpenGameAgent.Kernel`：进程内 C# 集成可以直接调用 `GameAgentRuntime`，完全不引用 Runtime 包。

当客户端需要断线重连、消费统一跨语言事件，或安全地插话/中断指定运行时使用它。公共包分工如下：

- `OpenGameAgent.Runtime.Protocol`：传输无关 DTO、JSON codec、能力协商和客户端 reducer；
- `OpenGameAgent.Runtime.Hosting`：进程内事件投影与有界重放 journal；
- `OpenGameAgent.Client`：类型化 HTTP/SSE 客户端；
- `OpenGameAgent.Server`：可自行托管的 HTTP/SSE 实现。

规范 Schema、fixture、无第三方依赖的 C++ DTO，以及生成的 TypeScript/Python SDK 位于 `protocol/runtime/v1`。商业服务只能消费这些公共契约，不需要复制服务器内部 DTO，更不能 fork Agent loop。

TypeScript 与 Python SDK 均包含严格 DTO 校验、游标解析、同语义客户端 reducer，以及 initialize、游标分页、精确 steer/interrupt 和可续传 SSE 客户端。TypeScript 没有运行时依赖并生成 JavaScript 与声明文件；Python 只依赖标准库。两者都由仓库内 v1 Schema 确定性生成并嵌入其 SHA-256。运行 `./tools/Test-RuntimeProtocolSdks.ps1` 可同时核验生成结果和打包后的全新消费者。

## 版本化分发

Runtime Protocol v1 已进入 `0.3.0-alpha.4` 源码线。请锁定同一个精确源码提交，并通过 `ProjectReference` 引用 `OpenGameAgent.Runtime.Protocol`、`OpenGameAgent.Runtime.Hosting` 与 `OpenGameAgent.Client`；Hosting 和 Client 的项目依赖会保持 Protocol 对齐。

发行流水线会生成 `RELEASE_MANIFEST.json`，记录包版本、完整源码提交、支持的 Runtime Protocol 版本、包 ID、资产大小和冻结 SHA-256。`SHA256SUMS.txt` 覆盖包括该 Manifest 在内的每个发行载荷，校验索引本身作为独立验证器。

## 坐标与生命周期

每个事件都包含单调递增的 `sequence`、稳定 `eventId`、`(sessionId, actorId, inputId)`，以及可选的 `runId`、`turn/turnId` 和 `itemId/itemKind`。Run、Turn 和 Item 使用 `started`、`delta`、`completed` 生命周期。消息、工具、持久动作、批准、交互、产物、委派、计划、媒体和状态共用同一个 envelope。

`GameRuntimeReducer` 会拒绝混合 Session、非连续序列、重复开始、未开始就完成以及 Run 身份变化。Run 终止时，仍处于打开状态的表现型 Item 会先收到明确的 `item_interrupted`，再收到最终结果。它不会创造或重复任何持久游戏动作。

## Server 端点

| 端点 | 用途 |
| --- | --- |
| `POST /runtime/v1/initialize` | 协商协议版本与能力 |
| `POST /runtime/v1/run/stream` | 幂等启动或重连到一个输入，并接收 SSE |
| `POST /runtime/v1/events` | 不建立长连接地读取有界游标页 |
| `POST /runtime/v1/control/steer` | 只插话到精确匹配的 Run/Turn |
| `POST /runtime/v1/control/interrupt` | 中断精确匹配的 Run 并等待其收束 |

Runtime 在访问自身状态或 Runtime 前，先执行和 v1 run、usage、transcript、approval、durable action 相同的身份派生所有者授权与宿主 audience 投影。不能设置 Header 的本地引擎客户端可以在 Body 提交有界凭证；凭证由宿主映射为 principal，所有权仍来自 principal，而不是 payload 自报的 Session/Actor。凭证不会进入 transcript、事件、journal、异常或结果。

公共 C# 客户端为 `GameRuntimeServerClient`：

```csharp
var client = new GameRuntimeServerClient(new GameRuntimeServerClientOptions(
    httpClient,
    new Uri("http://127.0.0.1:5157/")));

var negotiated = await client.InitializeAsync();
var cursor = await client.StreamAsync(
    input,
    requestId: "host-command-42",
    (value, cancellationToken) =>
    {
        engineQueue.Enqueue(value); // 之后在引擎主线程消费
        return default;
    });
```

相同的 `(session, actor, input, requestId, inputJson)` 表示重连。同一个 Input 若换成不同请求内容会 fail closed。

## 断线续传与对账

SSE 的 `id:` 是规范事件 ID。客户端持久记录最后一个完整应用的 ID，重连时发送 `Last-Event-ID`。某个 HTTP 调用者断开不会取消 Runtime 中的 Run；服务端只重放保留事件，不会再次调用模型。

事件保留是有界的。未知或过期游标会产生 `gap`，并标记 `requiresTranscriptReconciliation=true`。此时应停止增量归并，通过 `ServerGameAgentClient.ReadTranscriptAsync` 读取已授权持久 Transcript，重建 UI，再从页面的 `nextAfterSequence` 继续。Audience 投影过滤私密事件时，这个游标可能大于最后一条可见事件，避免重复扫描。

重连不能授权重复世界写入。非幂等工具仍必须经过 `DurableGameActionDispatcher`、`operationId`、journal、权威 receipt 和 reconcile；Runtime 只重放这些生命周期的观察结果。

## 精确控制

从事件流取得当前 `runId` 和 `turn`，同时写入 `GameRuntimeControlRequest`。延迟请求会返回 `runMismatch`、`turnMismatch`、`controlClosed` 或 `idle`，不能误操作更新的 Run。中断被接受后，端点会等 Runtime lane 收束并发出终态再返回。

旧的无坐标 `TrySteer`/`TryAbort` 仍适合紧耦合进程内代码；远程或可能延迟的客户端应始终使用精确坐标。

## 兼容规则

- 使用可选能力前必须协商，不能根据服务器品牌或版本字符串猜测。
- 新增可选 capability 或 payload 的附加字段，不得改变既有生命周期含义。
- 修改必需字段、枚举语义、游标规则或生命周期顺序，必须升级协议版本。
- JSON reader 拒绝重复属性，最大深度 128，并执行文档中的字符数与页大小边界。
- 事件序号上限为 `9007199254740991`，确保 C#、C++、TypeScript 与 Python 都能无损表示同一个整数。
- `payloadJson`、`inputJson`、`messageJson` 各自包含一个有界规范 JSON 值；它们复用已有 Runtime wire，不把 Provider 私有对象塞进 Runtime Schema。
- 非 internal 投影永远不能包含隐藏 reasoning、signature、私密消息、凭证和私密工具细节。

运行 `dotnet test tests/OpenGameAgent.Runtime.Protocol.Tests -c Release` 校验 fixture/Schema；运行 `dotnet test tests/OpenGameAgent.Server.Tests -c Release` 校验授权、投影、重放和精确控制。

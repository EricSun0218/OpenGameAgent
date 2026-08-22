# 本地模型、语音与媒体

`OpenGameAgent.Providers.Local` 是可选的本地组合包。它不捆绑、不自动下载模型；游戏或开发工具明确选择可信服务，并决定何时探测、预热、加载、卸载或获取模型。

所有本地能力复用托管 Provider 的同一契约：文本、结构化上下文、图片理解、推理与工具调用走 `IGameModelProvider`；实时 PCM16 语音走 `IRealtimeTransport`；图片、语音和视频生成走 `IGameMediaGenerator`；持久世界变更仍必须经过游戏自有工具、journal 与权威 receipt。

## 本地对话和视觉

`LocalGameModelPresets` 提供 Ollama、LM Studio、LocalAI、llama.cpp 与 vLLM 的显式 loopback 配置。`LocalGameModelEndpoint.ProbeAsync` 有界读取模型目录与健康状态；无法从服务证明的工具、视觉或推理能力默认不声明，必须由宿主配置 capability override。之后仍使用现有 `GameModelCatalog` 和 Agent 循环，不会创建第二套聊天运行时。

## 本地实时语音

若 LocalAI 或 Speaches 提供 OpenAI-compatible Realtime WebSocket，可以直接使用 `LocalRealtimePresets`。若 VAD、STT、TTS 是分离服务，则使用 `ComposableRealtimeTransport`：

```csharp
var transport = new ComposableRealtimeTransport(
    new LocalOpenAISpeechRecognizer(new(
        http,
        new Uri("http://127.0.0.1:8000/v1"))
    {
        Model = "whisper-1",
    }),
    new LocalOpenAISpeechSynthesizer(new(
        http,
        new Uri("http://127.0.0.1:8000/v1"))
    {
        Model = "kokoro",
        OutputFormat = LocalOpenAISpeechOutputFormat.Pcm16,
    }));
```

它组合会话级 `IGameVoiceActivityDetector`、并发安全的 `IGameSpeechRecognizer`、流式 `IGameSpeechSynthesizer`，再通过原有 `RealtimeConversationManager` 与 `GameRealtimeAgentBridge` 驱动同一个 `GameAgentRuntime`。内置能量 VAD 是确定性、有界的默认实现，也可以替换为 ONNX 或平台 VAD。

语音开始时，即使 TTS 还没有产生首帧，也会取消当前及排队的表现型语音；已经通过 journal 派发的权威游戏动作不会因插话撤销或重复。预滚、单段语音、队列、帧、字幕、请求/响应与 Provider 超时都有显式上限。OpenAI-compatible 本地适配器使用 WAV PCM16 输入，并验证 raw PCM16 或 WAV 输出；错误不会回显提示、响应正文或凭据。

## 本地模型生命周期

`LocalGameModelLifecycle` 提供显式的 inventory、warmup、load、unload 与 acquire 开发工具契约。Agent 运行和模型输出不会自动调用它。并发与超时有界；如果宿主没有配置 `AuthorizeAcquisitionAsync`，下载会在接触 backend 之前 fail-closed。

```csharp
using var lifecycle = new LocalGameModelLifecycle(
    new OllamaGameModelLifecycleBackend(
        new OllamaGameModelLifecycleOptions(http)),
    new LocalGameModelLifecycleOptions
    {
        AuthorizeAcquisitionAsync = (request, cancellationToken) =>
            new ValueTask<bool>(developerSettings.AllowModelDownloads),
    });

var models = await lifecycle.ReadInventoryAsync(true, cancellationToken);
await lifecycle.WarmupAsync("qwen2.5:7b", cancellationToken);
```

官方 Ollama backend 合并已安装与正在运行的模型，显式执行 keep-alive 加载/卸载，并有界解析 pull 进度。其他服务实现 `ILocalGameModelLifecycleBackend`。模型许可、磁盘位置、下载来源与最终授权始终属于宿主。

## Embedding 与媒体

- `LocalOpenAIEmbeddingProvider` 对接 `/v1/embeddings`，稳定的 provider/model/weights/dimensions identity 会让向量索引在模型变化时显式要求重建。
- `OpenGameAgent.Memory.Onnx` 可在进程内运行宿主随游戏分发的 BGE-M3 INT8 ONNX；框架不下载权重，也不要求 Python 服务。
- `LocalAiMediaProvider` 支持本地图片、视频和语音生成；输出经过大小、类型和字节签名验证。
- `ComfyUiMediaProvider` 只执行宿主提供的可信工作流，模型不能任意提交图。生成结果仍进入现有可恢复资产流水线并由游戏权威导入。

远程本地服务必须显式启用；非 loopback 明文 HTTP 还需要第二次明确许可。宿主负责安装、模型许可、硬件容量、引擎主线程切换和所有世界状态校验。

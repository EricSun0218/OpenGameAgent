# Windows 凭据持久化

[English](windows-credentials.md)

`OpenGameAgent.Models.Credentials.Windows` 是 `IGameCredentialStore` 的可选 Windows 专用实现，适用于桌面游戏、本地 sidecar 和自托管工具。它使用 Windows 数据保护 API（DPAPI）的 `CurrentUser` 作用域保存 Provider 凭据，不选择 Provider、不下载凭据，也不会建立第二套认证系统。

```csharp
using OpenGameAgent.Models;
using OpenGameAgent.Models.Credentials.Windows;

var directory = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
    "MyGame",
    "OpenGameAgent");
var credentials = new WindowsDpapiGameCredentialStore(
    new WindowsDpapiGameCredentialStoreOptions(directory)
    {
        Capacity = 32,
    });

var authentication = new StoredGameProviderAuthentication(
    providerId: "my-provider",
    store: credentials,
    schemes: new[] { "api-key" },
    login: async (_, interaction, cancellationToken) =>
    {
        var secret = await interaction.PromptAsync!(
            "Provider API key",
            true,
            cancellationToken);
        return new GameCredential(GameCredentialKind.ApiKey, secret);
    });
```

安全与持久化语义：

- 每条凭据的完整载荷（包括 Provider/Profile 标识和元数据）都使用 DPAPI `CurrentUser` 与全新随机 entropy 独立加密。
- 版本化 JSON 文档只包含 DPAPI 密文与随机 entropy，不会主动写入明文凭据、凭据摘要、Prompt 或 Provider 响应。
- 跨实例独占租约会串行化 `SetAsync`、`RemoveAsync` 以及 `ModifyAsync` 的完整读—修改—写单元。写入先落到同目录临时文件并刷盘，再原子替换；未提交就中断的临时文件会在下次操作时丢弃。
- 容量、明文大小、加密文档大小、锁等待、元数据和键格式都有上限。损坏、篡改、属于其他用户或版本不受支持的数据都会 fail-closed，绝不按明文降级。
- 如果现有目录组件或存储文件是符号链接/Windows 重解析点，构造或操作会拒绝。宿主仍应选择带正常 Windows ACL 的当前用户应用数据目录。
- 异常不会包含 Secret、密文、entropy 或由凭据派生的摘要；包本身不记录凭据内容。

`CurrentUser` 能阻止其他 Windows 账户解密文件，但以同一用户身份运行的进程通常也能调用 DPAPI。这是本机静态保护，不是针对已在玩家账户下运行的恶意软件的沙箱。换账号恢复备份或重置 Windows 用户配置后，旧文件可能无法解密；此时应要求重新认证，不能降级保存明文。

非 Windows 系统调用公开构造函数会得到 `PlatformNotSupportedException`。macOS 或 Linux 宿主应在同一个 `IGameCredentialStore` 接口后接入其原生安全存储。发布到玩家客户端的永久开发者上游 Key 依然无法对玩家保密；这种场景应使用玩家 BYOK、本地模型、开发者签发的短期凭证或可信服务端。

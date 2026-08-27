# 持久图片附件

OpenGameAgent 不把图片二进制写进规范对话记录。可选包 `@opengameagent/attachments` 会将 PNG、JPEG、GIF 和 WebP 观察写入有界、内容寻址的本地存储；对话中只保存不可变的 `imageRef`，其中包含内容哈希、MIME、字节数和尺寸。

```ts
import { LocalGameImageAttachmentStore } from "@opengameagent/attachments";
import { PiGameAgentKernel } from "@opengameagent/kernel-pi";

const imageAttachments = new LocalGameImageAttachmentStore({
  directory: "D:/my-game/save-data/agent-images",
  maximumBytes: 16 * 1024 * 1024,
  maximumPixels: 32 * 1024 * 1024,
});

const kernel = new PiGameAgentKernel({
  models,
  conversationStore,
  imageAttachments,
});
```

初始 `GameInput` 可以传入规范 Base64 图片，也可以传入已经接纳的 `imageRef`。配置附件存储后，内联图片会在保存对话前转换为引用；模型请求前，框架才解析引用并核验哈希和元数据。引用缺失、损坏或被替换时会在调用 Provider 前失败关闭。

本地存储具备以下特性：

- 使用 SHA-256 内容标识和原子目录发布；
- 相同图片并发写入时自动去重；
- 拒绝未知格式、MIME 不匹配、字节或像素超限、格式头损坏、部分写入、内容校验失败和重定向存储路径；
- 不隐式访问网络，也不下载模型；
- 公开对话投影只返回附件元数据，不返回图片字节。

没有附件存储时，一次性 Kernel 仍可直接使用内联图片。精确 `steer`/`followUp` 控制是同步接口，因此只接受内联图片；调用前应由宿主解析持久引用。初始运行和持久对话恢复都原生支持 `imageRef`。

图片附件是派生内容，不是游戏权威状态。游戏仍负责决定角色可以看到哪些截图或观察，并必须授权任何读取真实图片字节的接口。

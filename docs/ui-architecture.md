# 界面架构

## 1. 承载方式

界面不是独立窗口，而是 Revit 的 **DockablePane**：

- 入口：`RevitAi.Addin/RevitAiApplication.cs` 注册 Pane 与 Ribbon 按钮；
- 容器：`UI/AiPanelPage.cs` 承载 `WebView2`；
- 资源：`UI/index.html`、`UI/styles.css`、`UI/app.js`，同时以嵌入资源与输出文件两种方式分发。

## 2. 前后端消息

```
WebView2 (app.js)  ──postMessage──▶  AiPanelPage.cs  ──▶ AgentRuntimeService
        ▲                                   │
        └──────── hostObjects / eval ────────┘
```

消息以 JSON 传递，宿主侧按 `type` 分派。当前实现的类型：

| 方向 | type | 载荷 |
| --- | --- | --- |
| JS → C# | `chat` | 用户输入文本 |
| JS → C# | `chat.cancel` | 取消当前轮 |
| JS → C# | `attachment.add` / `attachment.clear` | 附件引用 |
| JS → C# | `get.status` | 请求宿主状态 |
| JS → C# | `reset` | 清空会话 |
| JS → C# | `settings.save` | 保存模型配置 |
| C# → JS | `host.status` | 宿主与 Agent 状态 |
| C# → JS | `chat.started` / `chat.user` | 一轮开始、用户消息落位 |
| C# → JS | `chat.progress` | 阶段进度（模型请求、工具执行） |
| C# → JS | `chat.toolResults` | 工具调用结果摘要 |
| C# → JS | `chat.response` | 最终答复（整段） |
| C# → JS | `chat.error` / `chat.cancelled` / `chat.finished` | 异常、取消、结束 |
| C# → JS | `chat.reset` / `sessions.notice` | 会话重置与提示 |

## 3. Agent 循环

`AgentLoop` 维持一轮对话的状态机：

1. 组装 system prompt、历史消息与工具清单；
2. 请求模型（一次请求一次响应）；
3. 若返回 `tool_calls`，逐个执行并回填结果；
4. 直到模型不再请求工具，输出最终答复；
5. 整轮结束后持久化会话。

工具执行通过 `IToolInvoker` 映射到工具仓库共用的 `BridgeRuntime`。App 与独立 Bridge 共用 145 项目录；每轮 Agent 绑定一个文档，取消或异常也保留已经完成的工具结果。服务级运行锁防止 UI 与外部 agent.chat 并发替换会话。

> 当前模型客户端以 `stream = false` 请求，界面通过 `chat.progress` 与 `chat.toolResults` 展示步骤，而非逐 token 渲染。若需要真正的流式输出，应在客户端增加 SSE 解析并在界面增量追加。

## 4. 附件处理

`AttachmentPreprocessor` 在发送前把附件转为模型可消费的形式：

- 图片：按 token 预算缩放；
- PDF：交给 `RevitAi.PdfWorker` 渲染为页面图像；
- CAD：由工具层的 CAD 读取能力解析。

PDF Worker 是独立进程，避免渲染库把宿主进程拖垮；依赖通过 NuGet 引入。

## 5. 会话与设置

- 会话：`AgentSessionStore` 以 JSON 存放在 `%LOCALAPPDATA%\RevitAi\sessions`；
- 设置：`RuntimeSettingsStore` 存放在 `%LOCALAPPDATA%\RevitAi\settings.json`，密钥经 DPAPI 保护。

两者都只保存本机数据，不上传。

## 6. 设计约束

- 界面不直接持有 Revit API 对象，所有模型操作经由工具契约；
- 界面线程不做几何计算；
- 流式渲染与工具执行解耦，工具进行中界面仍可显示进度；
- 关闭 Pane 不中断正在执行的事务。

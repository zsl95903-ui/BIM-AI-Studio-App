# UI 编写指南

本文说明如何为 BIM-AI Studio 重新实现一个上层界面。界面层与工具层之间只有两条契约：**工具调用**与**消息协议**。满足这两条，界面可以完全重写。

---

## 1. 界面在系统中的位置

```
你的界面
   │  ① 调用工具（IToolInvoker / 命名管道）
   ▼
AgentLoop ──▶ 模型服务
   │  ② 流式回传文本与工具事件
   ▼
你的界面
```

界面不直接持有 Revit API 对象，也不参与事务。它只负责：呈现对话、收集输入、显示工具进度、读写配置。

## 2. 两种承载方式

| 方式 | 适用 | 入口 |
| --- | --- | --- |
| DockablePane + WebView2 | 需要现代化排版、富文本、附件预览 | `IExternalApplication` + `IDockablePaneProvider` |
| 原生 WPF 窗口 | 需要与 Revit 原生控件一致 | `ExternalCommand` 或 `IExternalApplication` |

仓库中的参考实现使用第一种：`host/hybrid-ui/RevitAi.Addin`。

## 3. 最小实现步骤

### 3.1 注册加载项

```csharp
public sealed class MyApplication : IExternalApplication
{
    public Result OnStartup(UIControlledApplication app)
    {
        var provider = new MyPaneProvider();
        app.RegisterDockablePane(MyPane.Id, "BIM-AI Studio", provider);
        return Result.Succeeded;
    }

    public Result OnShutdown(UIControlledApplication app) => Result.Succeeded;
}
```

`.addin` 清单指向该类型，`Assembly` 与 `FullClassName` 必须一致。

### 3.2 承载界面

```csharp
public sealed class MyPaneProvider : IDockablePaneProvider
{
    public void SetupDockablePane(DockablePaneProviderData data)
    {
        data.FrameworkElement = new MyPaneControl();
        data.InitialState = new DockablePaneState
        {
            DockPosition = DockPosition.Right
        };
    }
}
```

WebView2 版本在 `MyPaneControl` 里初始化 `CoreWebView2`，把 `index.html` 以虚拟主机或 `NavigateToString` 载入。

### 3.3 定义消息协议

前后端只交换 JSON。建议保持下列类型名，便于复用现有宿主代码：

| type | 方向 | 载荷 |
| --- | --- | --- |
| `send` | UI → 宿主 | `text`、可选 `attachments` |
| `delta` | 宿主 → UI | `text`，增量 |
| `tool` | 宿主 → UI | `name`、`status`、`summary` |
| `done` | 宿主 → UI | 整轮结束 |
| `error` | 宿主 → UI | `message` |
| `settings.get` / `settings.save` | 双向 | 模型配置 |
| `session.list` / `session.load` | 双向 | 会话 |

宿主侧建议用一个 `switch (type)` 分派，未知类型直接忽略，避免版本前后不兼容。

### 3.4 调用工具

界面不直接调 Revit API，而是调用 `IToolInvoker`：

```csharp
var result = await invoker.InvokeAsync("create_straight_wall", new Dictionary<string, object>
{
    ["start_x"] = 0, ["start_y"] = 0,
    ["end_x"] = 6000, ["end_y"] = 0,
    ["level_id"] = levelId, ["wall_type_id"] = typeId,
    ["height"] = 3000
});
```

工具清单应通过注册表在运行时获取，不要写死在界面里。

### 3.5 流式呈现

模型返回是增量的。界面应：

1. 收到 `delta` 立即追加到当前消息；
2. 收到 `tool` 时插入一条工具活动条目，显示名称与状态；
3. 收到 `done` 后落盘会话；
4. 出错时保留已输出内容，并显示可复制的错误文本。

## 4. 配置界面

模型配置只有三个字段：`ApiUrl`、`Model`、`ApiKey`。界面负责收集，宿主负责加密落盘（DPAPI）。界面不得把明文 Key 写入日志或会话文件。

## 5. 可替换点

| 点 | 说明 |
| --- | --- |
| 布局 | 侧边栏 / 底部面板 / 独立窗口均可，不影响契约 |
| 渲染 | 纯 DOM、React、Vue 或原生控件均可 |
| 附件 | 图片、PDF、CAD 的处理由宿主完成，界面只传引用 |
| 主题 | 跟随 Revit 深色/浅色，或独立主题 |

## 6. 验收清单

- [ ] 面板可在 Revit 中停靠、浮动、关闭后重开
- [ ] 消息可流式显示，工具活动可见
- [ ] 配置保存后无需重启即可生效
- [ ] 会话可恢复
- [ ] 界面不直接引用 `Autodesk.Revit.DB` 做几何操作
- [ ] 关闭面板不会中断执行中的事务

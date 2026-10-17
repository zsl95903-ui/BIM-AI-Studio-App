# 参考界面说明

仓库中的参考实现位于 `host/hybrid-ui`，采用 DockablePane + WebView2。它只作为**结构参考**，可以整体替换。

## 1. 组成

| 文件 | 职责 |
| --- | --- |
| `RevitAi.HybridUI.addin` | 加载项清单 |
| `RevitAi.Addin/RevitAiApplication.cs` | 注册 Pane、Ribbon 按钮、启动宿主 |
| `RevitAi.Addin/UI/AiPanelPage.cs` | WebView2 控件与消息分派 |
| `RevitAi.Addin/UI/index.html` | 页面骨架 |
| `RevitAi.Addin/UI/styles.css` | 主题与布局 |
| `RevitAi.Addin/UI/app.js` | 交互逻辑与流式渲染 |

## 2. 界面结构

参考实现分为四个区域：

1. **顶部栏**：会话标题、模型指示、设置入口；
2. **消息区**：用户消息、助手消息、工具活动条目；
3. **输入区**：多行输入、附件按钮、发送；
4. **设置抽屉**：服务地址、模型名、密钥。

这些区域没有强制的视觉规范，替换实现只需保持第 3 节的消息类型。

## 3. 与宿主的交互

```javascript
// 发送
window.chrome.webview.postMessage({
  type: "send",
  text: input.value
});

// 接收
window.chrome.webview.addEventListener("message", (e) => {
  const msg = e.data;
  if (msg.type === "delta") appendText(msg.text);
  if (msg.type === "tool") upsertToolActivity(msg);
  if (msg.type === "done") finishTurn();
});
```

宿主侧在 `AiPanelPage.cs` 中解析并在 UI 线程更新。

## 4. 中性化说明

参考界面不包含任何账户、购买、额度或更新入口。模型配置只有地址、模型名、密钥三项，密钥由宿主加密保存。

## 5. 替换建议

- 保留消息类型与字段名，内部实现随意替换；
- 若换成原生 WPF，可直接调用 `AgentRuntimeService`，跳过 WebView2；
- 若换成外置窗口，注意 `Owner` 设为 Revit 主窗口，避免层级问题。

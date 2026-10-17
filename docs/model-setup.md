# 模型接入

## 1. 配置位置

```text
%LOCALAPPDATA%\RevitAi\settings.json
```

由宿主在首次保存时创建。字段：

| 字段 | 类型 | 说明 |
| --- | --- | --- |
| `ApiUrl` | string | OpenAI 兼容的 Chat Completions 基址，例如 `https://api.deepseek.com/v1` |
| `Model` | string | 模型标识，例如 `deepseek-flash` |
| `ProtectedApiKey` | string? | DPAPI 加密后的密钥；为空表示尚未配置 |

## 2. 密钥保护

`RuntimeSettingsStore` 使用 `ProtectedData.Protect`，作用域为当前用户（`DataProtectionScope.CurrentUser`），熵值为应用固定字符串。

因此：

- 配置文件不能跨用户复制后直接使用；
- 明文 Key 不落盘；
- 换机器需要重新填写。

## 3. 首次配置流程

1. 打开 Revit，加载本插件；
2. 打开侧边面板的设置页；
3. 填写 `ApiUrl`、`Model`、`ApiKey`；
4. 保存后宿主写入 `settings.json`（Key 已加密）；
5. 发送一条只读指令（例如「列出当前标高」）验证连通。

## 4. 兼容范围

客户端实现见 `host/hybrid-ui/RevitAi.Core/Agent/OpenAiCompatibleModelClient.cs`，遵循以下约定：

- `POST {ApiUrl}/chat/completions`
- `Authorization: Bearer {apiKey}`
- 请求体包含 `model`、`messages`、`tools`、`stream`
- 响应按 SSE 增量解析 `choices[].delta`

任何满足该形状的服务都可接入；不绑定特定服务商。

## 5. 工具调用

工具清单不由提示词硬编码，而是由工具层注册表在运行时生成 `tools` 数组。模型返回 `tool_calls` 后，宿主通过命名管道或进程内调用执行，并把结果作为 `tool` 消息回填。

这意味着：更换模型只需改 `ApiUrl`/`Model`，工具集合与调用契约不变。

## 6. 排错

| 现象 | 检查 |
| --- | --- |
| 提示未配置 | `settings.json` 是否存在、`ProtectedApiKey` 是否为空 |
| 401 / 403 | Key 是否有效、是否与 `ApiUrl` 匹配 |
| 404 | `ApiUrl` 是否包含 `/v1`，客户端会拼接 `/chat/completions` |
| 无工具调用 | 模型是否支持 function calling |

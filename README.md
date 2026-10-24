# BIM-AI Studio App

BIM-AI Studio 应用层：Revit 2027 加载入口、WebView2 界面、Agent 循环、
OpenAI-compatible 模型接入、PDF 处理与 MCP 集成。

工具契约和 120 个工具实现位于
[BIM-AI-Studio](https://github.com/zsl95903-ui/BIM-AI-Studio)，本仓库通过
Git submodule 以源码方式引用 `external/BIM-AI-Studio/toolset`。

项目自有代码采用 [Apache-2.0](LICENSE)。

> Not affiliated with or endorsed by Autodesk.

## 项目状态

- 主要目标平台为 Autodesk Revit 2027、Windows x64 和 .NET 10。
- 本项目由个人开发者维护，代码质量、兼容性和文档会持续改进。
- 自动化验证覆盖工具契约、模拟 Revit 适配器和应用层编译，不等同于对所有
  Revit 项目、族库、MEP 模板、CAD 文件和复杂建模流程的完整现场验证。
- 部分高级功能尚未在真实项目中充分测试，包括卫星图导入、复杂道路与楼梯、
  MEP 系统联动、批量族参数、CAD 文本提取和多 Revit 进程并发。
- 写入模型前应备份项目，并先在测试模型中验证工具调用。

## 组成

| 目录 | 内容 |
| --- | --- |
| `host/hybrid-ui` | 当前维护的加载入口、WebView2 UI、Agent 循环、PDF Worker 和工具兼容层 |
| `host/hybrid-ui/RevitAi.Core` | 模型消息、Agent 循环、会话状态与 OpenAI-compatible 客户端 |
| `host/hybrid-ui/RevitAi.Addin` | Revit 宿主、DockablePane、工具适配器、设置与 MCP 集成 |
| `external/BIM-AI-Studio` | 工具层源码子模块，目录名为 `toolset` |
| `docs` | 模型、界面、贡献和状态说明 |

## 兼容层

`host/hybrid-ui/RevitAi.Addin/Tools/ToolInvokerBridge.cs` 将当前 UI 的
`IToolInvoker` 契约映射到工具仓库的共用 `BridgeRuntime` / `ToolDispatcher`，与独立 Bridge 使用同一目录与执行器：

1. 合并 119 项启用主契约与 26 项专业扩展，共 145 个工具；
2. 校验 JSON Schema 后将参数传给主契约或专业绘图实现；
3. 通过 ExternalEvent 在实际执行前检查取消与目标文档身份；
4. 事务提交或回滚由工具宿主统一处理；
5. 结果统一映射为 UI 的 `ToolInvocationResult`，会话操作与外部 Agent 调用共用运行锁。

取消不会撤销已经提交的操作；桥接支持请求去重、取消与状态查询。详见工具仓库的协议 3.0 文档。

自动回归：`node tests/ui-regression.cjs` 与 `dotnet run --project tests/Agent.Runtime.Tests -c Release`。

## 模型接入

配置保存于 `%LOCALAPPDATA%\RevitAi\settings.json`，API Key 使用 Windows
DPAPI 保护。

```json
{
  "ApiUrl": "https://api.deepseek.com/v1",
  "Model": "deepseek-flash",
  "ProtectedApiKey": null
}
```

`OpenAiCompatibleModelClient` 支持 OpenAI Chat Completions、function tools、
文本与图片附件，以及兼容网关。详见
[docs/model-setup.md](docs/model-setup.md)。

## 构建

前置条件：

- Autodesk Revit 2027
- .NET 10 SDK
- 已初始化 `external/BIM-AI-Studio` 子模块

```powershell
git clone --recurse-submodules https://github.com/zsl95903-ui/BIM-AI-Studio-App.git
cd BIM-AI-Studio-App
powershell -ExecutionPolicy Bypass -File .\host\hybrid-ui\build.ps1
```

Revit 或工具层位于其他目录时：

```powershell
powershell -ExecutionPolicy Bypass -File .\host\hybrid-ui\build.ps1 `
  -RevitApiDir "D:\Autodesk\Revit 2027" `
  -ToolLayerDir "D:\src\BIM-AI-Studio\toolset"
```

## 验证范围

- 工具层构建通过，完整冒烟测试为 352 项通过、0 项失败。
- 当前 UI 已针对工具仓库 `toolset` 源码完成兼容编译。
- `OpenAiCompatibleModelClient` 与 Agent 循环通过编译验证。
- 真机模型上的完整写入矩阵、复杂 MEP、CAD 和外部模型服务仍需逐项验证。

## 文档

- [docs/project-status.md](docs/project-status.md)
- [docs/model-setup.md](docs/model-setup.md)
- [docs/ui-architecture.md](docs/ui-architecture.md)
- [docs/ui-authoring-guide.md](docs/ui-authoring-guide.md)

## 许可

项目自有代码采用 Apache-2.0。Autodesk Revit API 由用户本地安装提供，不随
本仓库分发。NuGet 与运行时依赖继续适用各自许可证。

# BIM-AI Studio App

BIM-AI Studio 的**应用层**：加载入口、上层界面、模型接入与 PDF 处理。

工具契约与 120 个工具的实现位于另一个仓库 [BIM-AI-Studio](https://github.com/zsl95903-ui/BIM-AI-Studio)，本仓库以源码方式引用它。

> Not affiliated with or endorsed by Autodesk.

---

## 仓库关系

```
BIM-AI-Studio           工具层：契约、注册表、服务、120 个工具
        ▲
        │ 源码引用（external/BIM-AI-Studio，git submodule）
        │
BIM-AI-Studio-App       应用层：加载入口、界面、模型接入、PDF
```

## 目录

| 目录 | 内容 |
| --- | --- |
| `host/hybrid-ui` | **可运行的加载入口**：`RevitAi.HybridUI.addin` + `RevitAi.Addin`（DockablePane + WebView2）+ 内置 Agent 循环 + PDF Worker |
| `host/production-bridge` | Codex 命名管道桥 |
| `src/RevitAi.Loader` | 旧版控制台入口的加载器（参考源码） |
| `src/RevitAi.Main` | 旧版控制台主体（参考源码） |
| `src/RevitAi.Commands` | 旧版 Ribbon 命令（参考源码） |
| `docs` | 模型接入与界面架构说明 |

> 旧版控制台（`src/RevitAi.*`）依赖账户模块，该模块不在开源范围内，因此这三个工程**仅作参考**，不保证可独立构建。可运行路径是 `host/hybrid-ui`。

## 构建

前置条件：

- Autodesk Revit 2027（提供 `RevitAPI.dll` / `RevitAPIUI.dll`）
- .NET 10 SDK

```powershell
git clone --recurse-submodules https://github.com/zsl95903-ui/BIM-AI-Studio-App.git
cd BIM-AI-Studio-App
dotnet build host\hybrid-ui\RevitAi.Addin\RevitAi.Addin.csproj -c Release
dotnet build host\hybrid-ui\RevitAi.PdfWorker\RevitAi.PdfWorker.csproj -c Release
```

Revit 装在非默认目录时：

```powershell
dotnet build host\hybrid-ui\RevitAi.Addin\RevitAi.Addin.csproj -c Release `
  -p:RevitApiDir="D:\Autodesk\Revit 2027" `
  -p:ToolLayerDir="D:\src\BIM-AI-Studio\rebuilt-120-tools"
```

## 依赖处理

两处原先依赖本机预编译产物或硬编码路径的地方，已改为标准依赖：

| 原方式 | 现方式 |
| --- | --- |
| 引用 `build/payload/app/R2027/RevitAi.*.dll` | `ProjectReference` 指向工具层源码 |
| `$(LocalDependencyDir)\UglyToad.PdfPig*.dll` | `PackageReference` `PdfPig` 0.1.16 |
| `$(LocalDependencyDir)\PdfWorker\PDFtoImage.dll` 等 | `PackageReference` `PDFtoImage` 5.4.0 |
| `ACadSharp` / `Newtonsoft.Json` / WebView2 本地 DLL | NuGet 包 |

## 模型接入

配置写在 `%LOCALAPPDATA%\RevitAi\settings.json`，API Key 以 Windows DPAPI 保护后存储。

```json
{
  "ApiUrl": "https://api.deepseek.com/v1",
  "Model": "deepseek-flash",
  "ProtectedApiKey": null
}
```

`ApiUrl` 兼容任何 OpenAI Chat Completions 形式的服务。首次使用时在界面的设置页填写地址、模型名与 Key，Key 由宿主加密后落盘。

详见 [docs/model-setup.md](docs/model-setup.md)。

## 界面

界面是 WebView2 承载的单页应用，宿主以 DockablePane 注册，前后端通过 `postMessage` 交换结构化消息。

详见 [docs/ui-architecture.md](docs/ui-architecture.md)。

## 许可

自有代码采用 Apache-2.0。工具层为衍生作品，其许可边界见工具仓库的 `THIRD_PARTY_NOTICES.md`。

# 参与贡献

## 范围

本仓库是应用层：加载入口、界面、模型接入、PDF 处理。工具契约与工具实现属于 [BIM-AI-Studio](https://github.com/zsl95903-ui/BIM-AI-Studio)。

## 约定

1. 界面不得直接操作 Revit API 对象，一律通过工具契约。
2. 新增消息类型时，保持向后兼容：未知类型必须被忽略而不是抛错。
3. 配置只保留 `ApiUrl` / `Model` / 密钥三项，密钥必须经 DPAPI 加密。
4. 不提交账户、计费、额度相关代码。

## 禁止提交

- 证书、密钥、明文 API Key；
- 用户模型、日志、会话；
- 第三方安装包与原始程序集；
- 本机绝对路径。

## 验证

```powershell
powershell -ExecutionPolicy Bypass -File .\host\hybrid-ui\build.ps1
```

提交说明中应区分自动测试、编译验证和真实 Revit 项目验证。未在真实项目中
执行的流程必须明确标注为“未实测”。

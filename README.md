# AntigravityPrefix · 会话前缀

Windows x64 免安装浮窗，为本机 Antigravity 会话添加或清除 emoji 前缀，支持可编辑列表、拖拽排序、完整面板与 mini bar。当前版本 **0.1.1**。

## 直接使用

固定发布目录为 [release](./release/)，退出旧版后双击其中的 `AntigravityPrefix.exe`。无需安装 .NET、Node、Python 或提供 API Key。目录保持固定，不生成压缩包，不包含测试夹具、日志和个人配置。

先在 Antigravity 选中会话，点击工具的编辑入口：自动打开通过对话顶部右上角 `⋯（More actions）→ Rename` 触发，零全局快捷键冲突；若自动打开受阻，也可手动点击会话右上角 `⋯ → Rename` 或侧栏 `⋯ → Rename`。工具识别到唯一可写内联输入框（`Edit title`）后，点击前缀或清除，写入后自动发送回车提交；完整模式非编辑态采用 `WS_EX_NOACTIVATE` 防失焦保护，并在 Antigravity 2.34.0 完成实机交互与持久化验收。工具只通过 Windows 官方可访问性接口（UIA3）操作，不修改 Antigravity 安装文件、数据库或内部状态。

编辑列表使用勾号保存、叉号取消。用户配置独立固定在系统 LocalApplicationData 下的 `./AntigravityPrefix/emoji-settings.json`，更新程序不会覆盖它，且与 `CodexPrefix` 完全隔离。

## 双仓库协同

本工程配置了两个 GitHub 远端：

- `origin`：私密开发仓（`areusa/AntigravityPrefix`）
- `public-release`：公开分发仓（`areusa/AntigravityPrefix-Release`，包含脱敏源码与白名单 `./release/` 交付件，提交前均经 `./scripts/check-privacy.ps1` 校验）

## 目录

| 目录 | 内容 |
| --- | --- |
| [src/AntigravityPrefix](./src/AntigravityPrefix/) | WPF 主程序、单主 HWND、原生 emoji 渲染及 UI 自动化 |
| [src/AntigravityPrefix/Validation](./src/AntigravityPrefix/Validation/) | 同程序集的隔离规则与原生界面验收，保留原 CLI 入口 |
| [assets](./assets/) | 应用图标原稿、MingCute 许可、固定 Unicode 数据及已确认动效参数 |
| [scripts](./scripts/) | 构建、隔离自检、固定目录发布和提交前隐私检查 |
| [docs](./docs/) | 开发验收与使用说明文档 |
| [release](./release/) | 发布目录（由 `./scripts/publish.ps1` 生成白名单交付件） |

## 构建与持续发布

需要 Windows x64、[global.json](./global.json) 指定的 .NET SDK 及 NuGet 访问能力。构建脚本可从任意工作目录调用：

```powershell
# 构建、自检通过后更新固定发布目录；不生成压缩包。
./scripts/publish.ps1
# 检查所有待提交文件中的私人路径、凭据与运行数据。
./scripts/check-privacy.ps1
```

完整原生交互回归须在可交互桌面运行，方法见 [开发与验收](./docs/development.md)。

## 配置与隐私

仓库不提交真实用户配置、日志、截图、采样、会话内容、密钥、PDB、构建缓存或旧版二进制。原始材料与只读 UIA 探针仅留在仓库外的本机归档。

第三方许可见 [THIRD-PARTY-NOTICES.txt](./THIRD-PARTY-NOTICES.txt)。

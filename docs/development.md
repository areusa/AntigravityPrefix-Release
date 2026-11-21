# 开发与验收

本文路径相对仓库根目录。主项目为 `./src/AntigravityPrefix/AntigravityPrefix.csproj`。验证代码位于项目的 `./Validation/`，仍编入同一程序集，保持原有内部类型访问与 CLI 行为。

```powershell
# 只发布构建中间结果；不更新正式发布目录。
dotnet publish ./src/AntigravityPrefix/AntigravityPrefix.csproj -c Release -o ./.build/manual
# 规则、资源、渲染、配置及物理自检。输出只在中间目录。
./.build/manual/AntigravityPrefix.exe --self-test
# 无界面的进程故障验证：不创建 UIA，不连接真实窗口。
./.build/manual/AntigravityPrefix.exe --automation-worker-test
# 有真实桌面时点击开始，保持测试窗口在前台。
./.build/manual/AntigravityPrefix.exe --bar-test-click-start
./.build/manual/AntigravityPrefix.exe --emoji-test-click-start
./.build/manual/AntigravityPrefix.exe --emoji-input-test
```

原生测试使用独立夹具及 `./fixture-data/`，不得复制真实会话或连接真实 Antigravity 做写入实验。

原生桌面测试受 Windows 前台锁影响，必须使用点击启动入口；后台自动启动失败不等于产品功能失败。自检、WPF 位图和尾帧不能证明真实 DWM 首帧。真实 Antigravity、IME/系统选择器和多屏边界仍以实际交互证据为准。

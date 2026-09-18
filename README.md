# RapidTap

一个轻量、免安装的 Windows 鼠标连点器（自动点击器），纯 C# WinForms 编写，单文件绿色版，开箱即用。

[![Release](https://img.shields.io/github/v/release/zkuangda/RapidTap)](https://github.com/zkuangda/RapidTap/releases/latest)
[![Downloads](https://img.shields.io/github/downloads/zkuangda/RapidTap/total)](https://github.com/zkuangda/RapidTap/releases)
[![License](https://img.shields.io/github/license/zkuangda/RapidTap)](LICENSE)

## 功能特性

- **按住触发，松开停止**：默认热键 `PageDown`，按住即开始连点，松开立即停止，无需二次按键切换状态
- **热键可自定义**：支持键盘上几乎任意按键作为触发键
- **点击间隔精细可调**：0.1ms 精度，支持 0.1ms ~ 1000ms 区间；间隔低于 2ms 时自动切换到高精度自旋等待，保证微秒级点击间隔的稳定性
- **三种鼠标按键**：左键 / 右键 / 中键任选
- **实时 CPS 显示**：每 200ms 刷新一次当前每秒点击次数
- **窗口置顶**：可选，方便在游戏或全屏应用上保持面板可见
- **单文件绿色版**：自包含发布（self-contained），无需安装 .NET 运行时，下载即用

## 下载

前往 [Releases](https://github.com/zkuangda/RapidTap/releases/latest) 页面下载最新的 `RapidTap.exe`，双击运行即可，无需安装。

> 首次运行如被 Windows SmartScreen 拦截，点击「更多信息」→「仍要运行」即可（原因是可执行文件未做代码签名）。

## 使用方法

1. 运行 `RapidTap.exe`
2. 设置点击间隔（毫秒）、鼠标按键类型
3.（可选）点击「点击设置」自定义触发热键
4. 将鼠标移动到目标位置，按住热键开始连点，松开热键停止

## 实现原理

- 使用 `SendInput` 系统调用模拟鼠标点击，不移动鼠标指针
- 使用全局低级键盘钩子（`WH_KEYBOARD_LL`）监听热键的按下与松开，而非 `RegisterHotKey`（后者收不到松开事件，无法实现"松开即停"）
- 连点循环运行在独立后台线程，通过 `volatile` 字段与 `Interlocked` 与 UI 线程通信，UI 线程绝不参与计时，避免界面卡顿影响点击精度
- 调用 `timeBeginPeriod(1)` 将系统时钟精度提升到 1ms；间隔小于 2ms 时改用 `Stopwatch` + `SpinWait` 忙等，突破 `Thread.Sleep` ~15.6ms 的默认调度粒度限制

## 从源码构建

需要 [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)。

```bash
git clone https://github.com/zkuangda/RapidTap.git
cd RapidTap

# 调试运行
dotnet run

# 发布单文件绿色版（对应 Releases 中的 RapidTap.exe）
dotnet publish RapidTap.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true
```

发布产物位于 `bin/Release/net8.0-windows/win-x64/publish/RapidTap.exe`。

## 免责声明

本工具仅用于学习交流、辅助测试、无障碍访问等合法场景。请遵守所使用软件/平台/游戏的服务条款，因违规使用本工具造成的任何后果由使用者自行承担。

## License

[MIT](LICENSE)

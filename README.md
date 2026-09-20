# RapidTap

一个轻量、免安装的 Windows 鼠标连点器（自动点击器），纯 C# WinForms 编写，**单文件 55 KB**，开箱即用。

[![Release](https://img.shields.io/github/v/release/zkuangda/RapidTap)](https://github.com/zkuangda/RapidTap/releases/latest)
[![Downloads](https://img.shields.io/github/downloads/zkuangda/RapidTap/total)](https://github.com/zkuangda/RapidTap/releases)
[![Size](https://img.shields.io/badge/exe-55%20KB-success)](https://github.com/zkuangda/RapidTap/releases/latest)
[![License](https://img.shields.io/github/license/zkuangda/RapidTap)](LICENSE)

## 功能特性

- **按住触发，松开停止**：默认热键 `PageDown`，按住即开始连点，松开立即停止，无需二次按键切换状态
- **热键可自定义**：支持键盘上几乎任意按键作为触发键
- **点击间隔精细可调**：0.1ms 精度，支持 0.1ms ~ 1000ms 区间；间隔低于 2ms 时自动切换到高精度自旋等待，保证微秒级点击间隔的稳定性
- **三种鼠标按键**：左键 / 右键 / 中键任选
- **实时 CPS 显示**：每 200ms 刷新一次当前每秒点击次数
- **窗口置顶**：可选，方便在游戏或全屏应用上保持面板可见
- **高 DPI 自适应**：声明 PerMonitorV2，125% / 150% / 175% 等系统缩放下按真实 DPI 重新排版，而非拉伸位图；窗口按内容自适应尺寸，不会裁字或遮挡
- **单文件 55 KB**：基于 Windows 自带的 .NET Framework 4.8，无需安装任何运行时，下载即用

## 系统要求

Windows 10 1903 及以上 / Windows 11 —— 这些系统**自带 .NET Framework 4.8**，无需额外安装。

更早的 Windows（7 / 8.1 / 10 早期版本）需要先安装 [.NET Framework 4.8 运行时](https://dotnet.microsoft.com/download/dotnet-framework/net48)。

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
- 界面全部由 `TableLayoutPanel` + `AutoSize` 构成，窗口尺寸由内容实测得出；DPI 感知写在应用清单里（而非 `App.config`），因此发布产物始终是单个 exe

## 为什么只有 55 KB

早期版本（v1.0.x）以 .NET 8 自包含方式发布，exe 达 **69 MB** —— 其中真正属于本项目的代码只有 52 KB，其余全是被打包进去的 .NET 运行时，光用不到的 WPF 就占了 43 MB。

v1.1.0 改为面向 **.NET Framework 4.8**：它是 Windows 10 1903+ / Windows 11 的系统自带组件，不需要随程序分发。产物因此从 69 MB 降到 55 KB，缩小约 1300 倍，而「双击即用、无需安装」的体验完全不变。

## 从源码构建

需要 [.NET SDK](https://dotnet.microsoft.com/download)（用于 `dotnet build`）和 .NET Framework 4.8 目标包（Visual Studio 或 [Developer Pack](https://dotnet.microsoft.com/download/dotnet-framework/net48) 均可提供）。

```bash
git clone https://github.com/zkuangda/RapidTap.git
cd RapidTap

dotnet build RapidTap.csproj -c Release
```

产物位于 `bin/Release/net48/RapidTap.exe`，就是 Releases 里分发的那个文件，目录中不会有任何附属文件。

## 免责声明

本工具仅用于学习交流、辅助测试、无障碍访问等合法场景。请遵守所使用软件/平台/游戏的服务条款，因违规使用本工具造成的任何后果由使用者自行承担。

## License

[MIT](LICENSE)

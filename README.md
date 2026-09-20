# RapidTap

一个轻量、免安装的 Windows 鼠标连点器（自动点击器），纯 C# WinForms 编写，**单文件 72 KB**，开箱即用。

[![CI](https://github.com/zkuangda/RapidTap/actions/workflows/ci.yml/badge.svg)](https://github.com/zkuangda/RapidTap/actions/workflows/ci.yml)
[![Release](https://img.shields.io/github/v/release/zkuangda/RapidTap)](https://github.com/zkuangda/RapidTap/releases/latest)
[![Downloads](https://img.shields.io/github/downloads/zkuangda/RapidTap/total)](https://github.com/zkuangda/RapidTap/releases)
[![Size](https://img.shields.io/badge/exe-72%20KB-success)](https://github.com/zkuangda/RapidTap/releases/latest)
[![License](https://img.shields.io/github/license/zkuangda/RapidTap)](LICENSE)

## 功能特性

- **两种触发方式**：按住热键连点松开即停，或按一下开始、再按一下停止
- **热键可自定义**：支持几乎任意按键，也支持 `Ctrl` / `Alt` / `Shift` 组合键
- **点击间隔精细可调**：0.1ms 精度，0.1ms ~ 1000ms 区间；也可以直接填「每秒点击次数」，两个输入框双向联动
- **点击位置可锁定**：跟随鼠标，或拾取一个固定屏幕坐标后一直点那里
- **三种鼠标按键**：左键 / 右键 / 中键任选
- **设置自动保存**：关闭时写入 `%AppData%\RapidTap\settings.ini`，下次打开原样恢复
- **实时 CPS 显示**：每 200ms 刷新一次当前每秒点击次数
- **窗口置顶**：可选，方便在游戏或全屏应用上保持面板可见
- **高 DPI 自适应**：声明 PerMonitorV2，125% / 150% / 175% 等系统缩放下按真实 DPI 重新排版；窗口按内容自适应尺寸，不会裁字或遮挡
- **单文件 72 KB**：基于 Windows 自带的 .NET Framework 4.8，无需安装任何运行时，下载即用

## 系统要求

Windows 10 1903 及以上 / Windows 11 —— 这些系统**自带 .NET Framework 4.8**，无需额外安装。

更早的 Windows（7 / 8.1 / 10 早期版本）需要先安装 [.NET Framework 4.8 运行时](https://dotnet.microsoft.com/download/dotnet-framework/net48)。

## 下载

前往 [Releases](https://github.com/zkuangda/RapidTap/releases/latest) 页面下载最新的 `RapidTap.exe`，双击运行即可，无需安装。

> 首次运行如被 Windows SmartScreen 拦截，点击「更多信息」→「仍要运行」即可（原因是可执行文件未做代码签名）。
>
> 每个 Release 都附带 SHA256 校验值，可用 `Get-FileHash RapidTap.exe -Algorithm SHA256` 比对。

## 使用方法

1. 运行 `RapidTap.exe`
2. 设置点击间隔（或直接填每秒点击次数）、鼠标按键类型
3.（可选）点击「点击设置」自定义触发热键，按 `Esc` 可取消录入
4.（可选）把「点击位置」改成「固定坐标」，点「拾取坐标」后在屏幕上点一下目标位置
5. 按热键开始连点

设置会在关闭程序时自动保存，删除 `%AppData%\RapidTap\settings.ini` 即可恢复默认。

### 关于热键的提醒

程序的键盘钩子**从不吞掉按键**，所以如果把热键设成不带修饰键的字母或数字（比如 `A`），你在任何程序里输入这个字符都会触发连点，而字符本身仍会照常输入。设置这类热键时程序会弹窗提醒。推荐使用 `F1`~`F12`、`PageDown` 这类功能键，或加上 `Ctrl` / `Alt` / `Shift`。

### 已知限制

以管理员身份运行的程序（部分游戏、部分系统工具）出于 Windows 的权限隔离机制，既收不到本程序发出的点击，也不会把按键透给本程序的钩子。在这类窗口上连点会没有反应。

## 实现原理

- 使用 `SendInput` 模拟鼠标点击；跟随鼠标时不移动指针，固定坐标时以归一化的绝对坐标一次性提交「移动 + 按下 + 抬起」
- 使用全局低级键盘钩子（`WH_KEYBOARD_LL`）监听热键的按下与松开，而非 `RegisterHotKey`（后者收不到松开事件，无法实现"松开即停"）
- 连点循环跑在一条**常驻**后台线程上，空闲时阻塞在条件变量上不占 CPU。不采用"每次启动新建线程"的写法，那样在长间隔下快速开关会让新旧线程同时点击，速度凭空翻倍
- 连点期间定期用 `GetAsyncKeyState` 核对热键是否真的还按着。钩子收不到管理员进程的按键，那条「松开」消息可能永远不到，没有这道兜底程序会一直点下去
- 仅在连点期间调用 `timeBeginPeriod(1)` 提升系统时钟精度，停止后立即还原，避免常驻拉高全系统定时器频率
- 间隔小于 2ms 时改用 `Stopwatch` + `SpinWait` 忙等，突破 `Thread.Sleep` 的调度粒度限制；大于等于 2ms 时等待条件变量，既让出 CPU 又能被「停止」立即打断
- 界面全部由 `TableLayoutPanel` + `AutoSize` 构成，窗口尺寸由内容实测得出；DPI 感知写在应用清单里（而非 `App.config`），因此发布产物始终是单个 exe

## 为什么只有 72 KB

v1.0.x 以 .NET 8 自包含方式发布，exe 达 **69 MB** —— 其中真正属于本项目的代码只有几十 KB，其余全是被打包进去的 .NET 运行时，光用不到的 WPF 就占了 43 MB。官方的裁剪功能对 WinForms 不可用（`NETSDK1175`）。

从 v1.1.0 起改为面向 **.NET Framework 4.8**：它是 Windows 10 1903+ / Windows 11 的系统自带组件，不需要随程序分发。产物因此降到几十 KB，而「双击即用、无需安装」的体验完全不变。

## 从源码构建

需要 [.NET SDK](https://dotnet.microsoft.com/download) 和 .NET Framework 4.8 目标包（Visual Studio 或 [Developer Pack](https://dotnet.microsoft.com/download/dotnet-framework/net48) 均可提供）。

```bash
git clone https://github.com/zkuangda/RapidTap.git
cd RapidTap

# 构建
dotnet build RapidTap.csproj -c Release

# 运行单元测试
dotnet test tests/RapidTap.Tests/RapidTap.Tests.csproj
```

产物位于 `bin/Release/net48/RapidTap.exe`，就是 Releases 里分发的那个文件，目录中不会有任何附属文件。

### 项目结构

| 文件 | 职责 |
|---|---|
| `MainForm.cs` | 界面、全局钩子、与系统交互的一切 |
| `ClickEngine.cs` | 连点循环与线程管理；点击动作以委托注入，不依赖窗体 |
| `ClickTiming.cs` | 间隔 / 速度换算等纯函数 |
| `HotkeyBinding.cs` | 热键绑定的表示、显示名与序列化 |
| `AppSettings.cs` | 设置文件的读写与解析 |
| `tests/RapidTap.Tests/` | 单元测试（63 个），以链接源文件的方式引用上面几个不依赖窗体的类型 |

打 `v*` 标签会触发 [release.yml](.github/workflows/release.yml) 自动构建、跑测试、计算 SHA256 并发布 Release。

## 免责声明

本工具仅用于学习交流、辅助测试、无障碍访问等合法场景。请遵守所使用软件/平台/游戏的服务条款，因违规使用本工具造成的任何后果由使用者自行承担。

## License

[MIT](LICENSE)

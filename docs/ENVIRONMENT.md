# 本机运行与编译环境

检查日期：2026-10-22。只读检查，本轮未安装软件、运行时、SDK、依赖或第三方客户端。

## 当前方案无需补装

首版采用原生 C# / WPF 桌面程序，使用系统已有 .NET Framework 4.8 与 C# 5 编译器。不依赖 Node、Python、WebView2、浏览器或远端 API。

| 环境 | 当前证据 |
| --- | --- |
| .NET Framework | 注册版本 4.8.10008，Release 534296 |
| 现有 C# 编译器 | `C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe`，实际运行版本 14.8.10001.0，C# 5 |
| WPF 桌面程序集 | 同目录的 `WPF` 内存在 PresentationFramework.dll、PresentationCore.dll、WindowsBase.dll；System.Xaml.dll 也存在 |
| Framework MSBuild | 同目录存在 MSBuild.exe，实际版本 4.8.10001.0 |
| .NET Runtime | `C:\Program Files\dotnet\dotnet.exe` 报告 Microsoft.NETCore.App 6.0.16；**没有 .NET SDK**，没有现代 Microsoft.WindowsDesktop.App |
| Node / npm | `C:\Program Files\nodejs`，实际运行 Node 20.17.0、npm 10.8.2 |
| MinGW C/C++ | PATH 外的 `D:\app\MinGW\bin`，实际运行 GCC/G++ 11.2.0、GNU Make 4.3 |
| WebView2 Runtime | `C:\Program Files (x86)\Microsoft\EdgeWebView\Application\155.0.4283.62`，核实版本 155.0.4283.62；不等于已经具备开发用 SDK |
| Git | PATH 中 `D:\app\Git\cmd\git.exe`，实际版本 2.55.0.windows.4；项目现已初始化为 Git 仓库，初始分支为 main |

Codex 本身附带 Python 3.12.14 和 Node 24.19.0，不能将这类应用自带运行时当作用户已独立安装的开发环境。本项目的编译与运行未依赖它们。

## 缺少或未检测到的环境

- **确证缺少现代 .NET SDK**：当前 dotnet 安装目录没有 SDK，命令列出的也只有 Runtime。现有 .NET 6 Runtime 不能用来编译现代 .NET 项目。
- **未检测到 Visual Studio / Build Tools、Rust/Cargo、CMake、Qt 和独立 Python 安装**。检查覆盖 PATH、卸载注册表、相关工具注册表、常见安装目录、用户目录和 `D:\app`；这不能排除其他自定义位置的便携版。
- Visual Studio 的常见目录存在但为空，两处标准 vswhere.exe 路径不存在。

上述工具均不是当前首版的前置条件。若将来迁移现代 .NET，应由用户自行准备合适的 .NET SDK / 桌面开发组件；若选 Tauri 才需要相应 Rust 与 C++ 构建组件；若选 Qt 才需要匹配的 Qt SDK。当前不需要为了此软件把这些全部安装一遍。

## 本轮验证

项目构建脚本直接调用已存在的 Framework 编译器，生成 Windows EXE；核心自动测试验证九宫格、方位、高差、可达范围、仰角边界和弹道回代。桌面验证覆盖 WPF 初始化、默认计算、非法输入清空与恢复、置顶切换、缩放复位和截图渲染。最终结果与截图保留于 `build/`。

0.2 地图资源下载与 WebP 转 JPEG 使用本机已有的 Codex 附带 Python/Pillow（12.3.0，已具备 WebP 解码），未安装任何依赖。软件运行和现有资源齐全后的编译仍只使用系统 .NET Framework。地图数据测试覆盖 26 张底图、25 张高程及完整地图标识；桌面验证逐张切换地图，检查自动高程、真实宽高、Belaya 手动模式和复制按钮移除。

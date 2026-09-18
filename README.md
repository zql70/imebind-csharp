# ImeBind（C# 版）

**按前台程序自动切换输入法** —— 给指定程序绑定指定的 Windows 输入法（TSF 输入法），程序切到前台时自动切换，离开时还原。

场景举例：系统日常用「微信输入法」，但打某个游戏时自动切成「微软拼音」（系统原生、进程外实现，不会把输入法代码注入游戏进程）。

> **Rust 版**：https://github.com/zql70/imebind-rs —— 功能相同，常驻私有内存约 1.8 MB（本版 22 MB），不依赖 .NET Framework，单 exe 零依赖。两个版本用同一个单实例互斥量，**不能同时运行**。

---

## English

ImeBind binds a specific Windows input method (a TSF text service) to a specific application: when the app's window comes to the foreground, the configured input method is activated; when you leave, the previous one is restored.

It exists because **Windows has no built-in per-application input method binding**, and every existing tool (KBLAutoSwitch, ImTip, AlwaysEnglish, …) works by posting `WM_INPUTLANGCHANGEREQUEST` with a keyboard layout handle (HKL) — which **cannot distinguish two TSF input methods that share one HKL** (on a Chinese (Simplified) system both 微软拼音 and a third-party TSF IME use `0x08040804`). The only way to select a specific TSF profile is `ITfInputProcessorProfileMgr::ActivateProfile`, which is what this tool calls.

Requires Windows 10/11 and .NET Framework 4.x (preinstalled). Build: `pwsh -NoProfile -File tools\build.ps1`. Usage: run `ImeBind.exe`, edit `rules.txt`. See below for details (Chinese).

A Rust rewrite with the same features but ~1.8 MB private memory (and no .NET dependency) is at https://github.com/zql70/imebind-rs.

---

## 它解决什么问题

Windows **没有**"给某个程序绑定某个输入法"的原生功能：

- 设置应用里跟输入法相关的选项只有「选择始终默认使用的输入法」，那是全局的；
- Windows 7 时代那个「允许我为每个应用窗口使用不同的输入法」复选框，在 Windows 10/11 上已经不存在了；
- 现有的第三方工具（`flyinclouds/KBLAutoSwitch`、`aardio/ImTip`、`ChaXxl/AlwaysEnglish`、`CodeInDreams/AHK-AutoSogouIME` 等）都是靠 `WM_INPUTLANGCHANGEREQUEST` + **键盘布局码 HKL** 实现的。搜狗这类老式 IMM32 输入法有自己专属的 HKL（如 `E0200804`）、英文有 `00000409`，所以能被点名；但**两个共用同一个 HKL 的 TSF 输入法之间无法用这种方式区分**（中文(简体)下 HKL 都是 `0x08040804`）。

要选中一个**具体的** TSF 输入法，只能调用 TSF 的 `ITfInputProcessorProfileMgr::ActivateProfile`。ImeBind 就是这么一个最小实现。

## 功能

- 按前台程序自动切换输入法，离开时还原
- 边沿触发：只在程序切换时动作一次（1.5 秒内补偿一次），不会和你手动切换输入法抢
- **托盘图标 + 右键菜单**：查看状态与已生效规则、暂停/恢复自动切换、重新加载 `rules.txt`、编辑规则、查看日志、打开程序目录、退出（退出时会还原输入法）
- 双击托盘图标弹出气泡显示当前状态
- 托盘图标按系统 DPI 取对应的帧（100%→16px、125%→20px、150%→24px、200%→32px），高缩放下不会虚
- 单实例运行，避免多个实例互相打架
- `--list` 直接列出本机可用输入法及其 TIP 字符串，不用去翻注册表
- 不注入任何进程、不联网、无遥测

## 环境要求

- Windows 10 / 11
- .NET Framework 4.x（系统自带）
- 编译需要 `csc.exe`（`C:\Windows\Microsoft.NET\Framework64\v4.0.30319\` 里自带）

## 目录结构

```
ImeBind.exe            编译产物（已 gitignore）
icon.ico               托盘图标 + exe 图标，由 tools/make-icon.ps1 生成
rules.txt              规则配置：程序名 = 输入法TIP
imebind.log            运行日志（已 gitignore）
src/ImeBind.cs         C# 源码
tools/                 开发与诊断脚本（构建、图标生成、状态检查、字体预览）
scripts/               面向用户的辅助脚本（开机自启的安装 / 卸载）
```

`ImeBind.exe`、`icon.ico`、`rules.txt`、`imebind.log` 必须在**同一个目录**下：程序运行时是按 exe 所在目录去找另外三个文件的。

## 构建

```powershell
pwsh -NoProfile -File tools\build.ps1
```

编译产物 `ImeBind.exe` 输出到仓库根目录。`build.ps1` 里的 `/codepage:65001` 是必需的：`csc` 对无 BOM 的源文件按 ANSI 代码页解码，中文注释和字符串会乱掉。

## 图标

`icon.ico` 由 `tools\make-icon.ps1` 生成，字形取自 **Windows 自带的 `Segoe Fluent Icons` 字体** —— 微软官方的图标字体，本来就是给 Windows 应用 UI 用的，不需要从外面下载素材、也没有第三方许可问题。

```powershell
pwsh -NoProfile -File tools\make-icon.ps1 -Preview                    # 生成候选字形对照表（icon-preview.png）
pwsh -NoProfile -File tools\make-icon.ps1 -Glyph E765 -Out icon.ico   # 用指定字形生成多尺寸 ICO
```

也可以直接把自己或设计师做好的 `.ico` 覆盖过去，`tools\build.ps1` 会自动用上。

微软的字体许可允许"用它创建、显示、打印内容"，但**不允许把字体文件本身再分发**——所以仓库里只提交渲染出来的 `icon.ico`，不要提交 `.ttf`。

## 使用

```powershell
ImeBind.exe                            # 后台常驻（带托盘图标），按 rules.txt 自动切换
ImeBind.exe --no-tray                  # 后台常驻但不建托盘图标（无界面模式，适合脚本/服务场景）
ImeBind.exe --list                     # 列出本机可用输入法（拿到 TIP 字符串）
ImeBind.exe --status                   # 打印前台程序与当前输入法
ImeBind.exe --menu-dump                # 打印托盘菜单内容（自检用，不点鼠标也能验证菜单逻辑）
ImeBind.exe --activate <TIP> [mode]    # 手动激活某个输入法；mode: both|session|process
```

**托盘菜单**内容：

```
状态：运行中
前台程序：xxx.exe
────────────
已生效的规则
  dota2.exe → 微软拼音
────────────
暂停自动切换 / 恢复自动切换
重新加载 rules.txt
────────────
编辑 rules.txt
查看日志
打开程序所在文件夹
────────────
退出
```

规则行显示的是输入法的**显示名**（由 TSF 的 `GetLanguageProfileDescription` 解析，结果有缓存）；解析失败时才退回原始的 TIP 字符串——直接显示 TIP 会把菜单撑得极宽。

Win11 默认会把新出现的托盘图标收进折叠区（点任务栏的 `^` 才能看到）。要让它常显，把它从折叠区拖出来即可；这个状态记在 `HKCU\Control Panel\NotifyIconSettings\<hash>\IsPromoted`。

从终端运行时输出会同时打印到控制台和 `imebind.log`；双击运行时只有日志。停止：任务管理器结束 `ImeBind.exe`，或

```powershell
Stop-Process -Name ImeBind -Force
```

开机自启：

```powershell
pwsh -NoProfile -File scripts\install-autostart.ps1     # 在启动文件夹建快捷方式
pwsh -NoProfile -File scripts\uninstall-autostart.ps1   # 撤销（并结束正在运行的实例）
```

## rules.txt

每行一条规则，`#` 开头为注释，程序名不区分大小写（只匹配 exe 文件名，不写路径）：

```
dota2.exe = 0804:{81D4E9C9-1D3B-41BC-9E6C-4B40BF79E35E}{FA550B04-5AD7-411F-A5AC-CA038EC515D7}
```

TIP 字符串用 `ImeBind.exe --list` 拿，格式是 `语言ID:{TIP的CLSID}{Profile的GUID}` —— 和 PowerShell 里 `Get-WinUserLanguageList` 输出的 `InputMethodTips` 是同一种写法，可以直接互换。

文件不存在时会自动生成一份带注释的模板。第一次运行时会提示"rules.txt 里没有有效规则"。

## 工作原理

1. 每 250ms 取一次前台窗口，从窗口所属进程拿到 exe 文件名；
2. exe 变化时查规则：命中就用 `ITfInputProcessorProfileMgr::ActivateProfile` 激活指定输入法，并记住切换前的输入法；离开规则程序时还原；
3. 激活前后都会用 `GetActiveProfile` 读一次当前输入法，切换结果记进日志。

调用的关键点（TSF 是那个"能区分具体输入法"的 API）：

```csharp
mgr.ActivateProfile(TF_PROFILETYPE_INPUTPROCESSOR /*1*/, langid,
                    ref clsid, ref guidProfile, IntPtr.Zero,
                    TF_IPPMF_FORPROCESS | TF_IPPMF_FORSESSION);
```

## 已知限制

- **按进程名匹配**，不支持通配符、窗口标题、窗口类名。
- 改动 `rules.txt` 后需要在托盘菜单点一下「重新加载 rules.txt」；没有做文件监听自动重载。
- 只处理 TSF 输入法（`TF_PROFILETYPE_INPUTPROCESSOR`），纯键盘布局（`TF_PROFILETYPE_KEYBOARDLAYOUT`）不参与规则。
- 用轮询而非 `SetWinEventHook`，占用极低但不如事件钩子优雅。
- 商店/MSIX 应用要注意：它们的窗口属于 `ApplicationFrameHost.exe`，所以规则里写 `charmap.exe` 这类程序名不会命中（写 `ApplicationFrameHost.exe` 又会影响所有商店应用）。
- 程序被强杀（任务管理器结束进程）时，如果当时正停留在规则程序里，输入法会停在规则指定的那个；从托盘菜单「退出」则会先还原。
- 托盘交互基于 WinForms（`System.Windows.Forms`），比无界面模式多占一些内存（实测约 30 MB）。
- 未做代码签名，部分杀毒软件可能对"常驻 + 监听前台窗口"的行为报警；源码和构建脚本都在仓库里，可自行编译。

## 隐私

ImeBind 只做三件事：读前台窗口所属进程的**文件名**、调用 TSF 切换输入法、写本地日志。不注入其他进程、不联网、无遥测。日志里会记录你前台程序的 exe 名（例如 `chrome.exe`），如果不想留下这些记录，删掉 `imebind.log` 或把 `rules.txt` 之外的文件一并清理即可。

## 致谢与许可

- TSF 的调用序列（`ChangeCurrentLanguage` + `ActivateProfile(TF_IPPMF_FORPROCESS|TF_IPPMF_FORSESSION)`）参考了 [Seelen-UI](https://github.com/eythaann/Seelen-UI)（AGPL-3.0）中 `system_settings/language/application.rs` 的做法。本项目为独立编写的 C# 实现，常量与接口签名均取自公开的 `msctf.idl`；如果你在意许可洁净度，可以在上面注明出处，或直接把本项目也改为 AGPL-3.0。
- 接口与方法顺序核对自 Microsoft 的 `msctf.idl`（win32metadata）与 wine 的 `msctf.idl`。

本项目采用 [MIT 许可](LICENSE)。

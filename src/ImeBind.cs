// ImeBind —— 按前台程序自动切换 Windows 输入法（TSF 输入法）
//
// 为什么需要它：Windows 没有"按应用绑定输入法"的原生功能。而所有靠键盘布局码(HKL)的方案
// （WM_INPUTLANGCHANGEREQUEST / LoadKeyboardLayout）都无法区分共用同一个 HKL 的两个 TSF 输入法
// （例如中文(简体)下的"微软拼音"和第三方 TSF 输入法，两者 HKL 都是 0x08040804）。
// 唯一可行的办法是调用 TSF 的 ITfInputProcessorProfileMgr::ActivateProfile。
//
// 编译（.NET Framework 4.x，C# 5）：直接用 tools\build.ps1 即可，它等价于：
//   csc /nologo /target:winexe /codepage:65001 /r:System.Windows.Forms.dll /r:System.Drawing.dll
//       /win32icon:icon.ico /out:ImeBind.exe src\ImeBind.cs
//   （icon.ico 和 rules.txt 要和 exe 放在同一目录，运行时按 exe 所在目录查找）
//   /codepage:65001 是必需的：csc 对无 BOM 的源文件按 ANSI 解码，中文注释和字符串会乱掉。
//
// 用法：
//   ImeBind.exe                            后台常驻（带托盘图标），按 rules.txt 自动切换
//   ImeBind.exe --no-tray                  后台常驻，但不创建托盘图标（无界面模式）
//   ImeBind.exe --list                     列出本机已安装的输入法及其 TIP 字符串
//   ImeBind.exe --status                   打印前台程序与当前输入法
//   ImeBind.exe --menu-dump                打印托盘菜单的内容（自检用，不点鼠标也能验证）
//   ImeBind.exe --activate <TIP> [mode]    手动激活指定 TIP（mode: both|session|process）
//
// 托盘菜单：状态与规则一览 / 暂停·恢复自动切换 / 重新加载 rules.txt / 编辑 rules.txt /
//           查看日志 / 打开程序所在文件夹 / 退出（退出时会还原输入法）
//
// rules.txt 每行一条规则：程序名 = 输入法TIP   （程序名不区分大小写，# 开头为注释）
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

static class Native
{
    [DllImport("user32.dll")]
    public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")]
    public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern bool QueryFullProcessImageName(IntPtr hProcess, uint flags, StringBuilder name, ref uint size);
    [DllImport("kernel32.dll")]
    public static extern bool CloseHandle(IntPtr h);
    // 必须 SetLastError=true，否则 GetLastWin32Error() 读不到 CreateMutex 设置的 ERROR_ALREADY_EXISTS
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern IntPtr CreateMutex(IntPtr attr, bool initialOwner, string name);
    [DllImport("kernel32.dll")]
    public static extern bool AttachConsole(int pid);
    [DllImport("ole32.dll")]
    public static extern void CoTaskMemFree(IntPtr p);
    // 托盘图标要按系统 DPI 选对帧，否则 125%/150% 缩放下会被拉伸虚化。
    // 注意：进程若没有 DPI 感知声明，GetDpiForSystem 恒返回 96，所以要先 SetProcessDPIAware。
    [DllImport("user32.dll")]
    public static extern uint GetDpiForSystem();
    [DllImport("user32.dll")]
    public static extern bool SetProcessDPIAware();
    [DllImport("user32.dll")]
    public static extern bool SetProcessDpiAwarenessContext(IntPtr context);
}

// 显式布局，按 x64 对齐：4 + 4(langid+padding) + 16 + 16 + 16 + 8 + 4(+4 padding) + 8 + 4 = 88 字节
[StructLayout(LayoutKind.Explicit)]
struct TF_INPUTPROCESSORPROFILE
{
    [FieldOffset(0)] public uint dwProfileType;
    [FieldOffset(4)] public ushort langid;
    [FieldOffset(8)] public Guid clsid;
    [FieldOffset(24)] public Guid guidProfile;
    [FieldOffset(40)] public Guid catid;
    [FieldOffset(56)] public IntPtr hklSubstitute;
    [FieldOffset(64)] public uint dwCaps;
    [FieldOffset(72)] public IntPtr hkl;
    [FieldOffset(80)] public uint dwFlags;
}

[ComImport, Guid("33C53A50-F456-4884-B049-85FD643ECFED")]
class TF_InputProcessorProfiles { }

// 注意：IEnumTfInputProcessorProfiles 的方法顺序是 Clone, Next, Reset, Skip（不是常见的 Next 在前）
// 它的 IID {71C6E74D-...} 与 ITfInputProcessorProfileMgr 的 {71C6E74C-...} 只差最后一位，别搞混
[ComImport, Guid("71C6E74D-0F28-11D8-A82A-00065B84435C"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IEnumTfInputProcessorProfiles
{
    [PreserveSig] int Clone(out IntPtr ppEnum);
    [PreserveSig] int Next(uint ulCount, [Out, MarshalAs(UnmanagedType.LPArray)] TF_INPUTPROCESSORPROFILE[] pProfile, out uint pcFetch);
    [PreserveSig] int Reset();
    [PreserveSig] int Skip(uint ulCount);
}

// vtable 顺序必须与 msctf.idl 一致。只有被调用的方法需要正确签名，其余作为占位即可
// （占位方法不会被调用，但必须存在，否则后面的槽位会错位）。
[ComImport, Guid("1F02B6C5-7842-4EE6-8A0B-9A24183A95CA"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface ITfInputProcessorProfiles
{
    [PreserveSig] int Register(ref Guid rclsid);                                                                          // 1
    [PreserveSig] int Unregister(ref Guid rclsid);                                                                        // 2
    [PreserveSig] int AddLanguageProfile(ref Guid rclsid, ushort langid, ref Guid guid, IntPtr desc, uint cchDesc, IntPtr file, uint cchFile, IntPtr hkl); // 3
    [PreserveSig] int RemoveLanguageProfile(ref Guid rclsid, ushort langid, ref Guid guid);                               // 4
    [PreserveSig] int EnumInputProcessorInfo(out IntPtr pEnum);                                                           // 5
    [PreserveSig] int GetDefaultLanguageProfile(ushort langid, ref Guid catid, out Guid clsid, out Guid guid);            // 6
    [PreserveSig] int SetDefaultLanguageProfile(ushort langid, ref Guid clsid, ref Guid guid);                            // 7
    [PreserveSig] int ActivateLanguageProfile(ref Guid clsid, ushort langid, ref Guid guid);                              // 8
    [PreserveSig] int GetActiveLanguageProfile(ref Guid clsid, out ushort langid, out Guid guid);                         // 9
    [PreserveSig] int GetLanguageProfileDescription(ref Guid clsid, ushort langid, ref Guid guid, out IntPtr desc);       // 10
    [PreserveSig] int GetCurrentLanguage(out ushort langid);                                                              // 11
    [PreserveSig] int ChangeCurrentLanguage(ushort langid);                                                               // 12
    [PreserveSig] int GetLanguageList(out IntPtr ppLangId, out uint pulCount);                                            // 13
}

[ComImport, Guid("71C6E74C-0F28-11D8-A82A-00065B84435C"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface ITfInputProcessorProfileMgr
{
    [PreserveSig] int ActivateProfile(uint dwProfileType, ushort langid, ref Guid clsid, ref Guid guidProfile, IntPtr hkl, uint dwFlags); // 1
    [PreserveSig] int DeactivateProfile(uint a, ushort b, ref Guid c, ref Guid d, IntPtr e, uint f);                       // 2
    [PreserveSig] int GetProfile(uint a, ushort b, ref Guid c, ref Guid d, IntPtr e, IntPtr f);                            // 3
    [PreserveSig] int EnumProfiles(ushort langid, out IEnumTfInputProcessorProfiles pEnum);                                // 4
    [PreserveSig] int ReleaseInputProcessor(ref Guid clsid, uint dwFlags);                                                // 5
    [PreserveSig] int RegisterProfile(ref Guid clsid, ushort langid, ref Guid guid, IntPtr desc, uint cchDesc, IntPtr icon, uint cchIcon, IntPtr hkl, uint prefLayout, int enabledByDefault, uint dwFlags); // 6
    [PreserveSig] int UnregisterProfile(ref Guid clsid, uint dwFlags);                                                    // 7
    [PreserveSig] int GetActiveProfile(ref Guid catid, ref TF_INPUTPROCESSORPROFILE p);                                    // 8
}

class Rule
{
    public string Exe = "";
    public ushort Langid;
    public Guid Clsid;
    public Guid Profile;
    public string Tip = "";
}

class Program
{
    const uint TF_PROFILETYPE_INPUTPROCESSOR = 0x0001;
    const uint TF_IPPMF_FORPROCESS = 0x10000000;
    const uint TF_IPPMF_FORSESSION = 0x20000000;
    const int ERROR_ALREADY_EXISTS = 183;
    static readonly Guid CAT_KEYBOARD = new Guid("34745C63-B2F0-4784-8B67-5E12C8701A31");

    static string dir;
    static string logPath;
    static bool hasConsole;
    static ITfInputProcessorProfiles profiles;
    static ITfInputProcessorProfileMgr mgr;
    static readonly object logLock = new object();

    // 常驻状态
    static List<Rule> rules = new List<Rule>();
    static string forcedTip = null;              // 切换前的输入法，离开规则程序（或退出）时还原
    static string seenExe = null;
    static DateTime reassertUntil = DateTime.MinValue;
    static bool paused = false;
    static uint FlagMode = TF_IPPMF_FORPROCESS | TF_IPPMF_FORSESSION;

    // 托盘
    static NotifyIcon tray;
    static ContextMenuStrip menu;
    static string lastTrayText = "";

    // 日志同时写文件和（若从终端启动）父控制台——winexe 自己没有控制台，
    // 不 AttachConsole 的话用户执行 --list 会看不到任何输出。
    static void Log(string msg)
    {
        try
        {
            lock (logLock)
            {
                if (hasConsole) Console.WriteLine(msg);
                if (File.Exists(logPath) && new FileInfo(logPath).Length > 262144) File.Delete(logPath);
                File.AppendAllText(logPath,
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture) + "  " + msg + "\r\n",
                    Encoding.UTF8);
            }
        }
        catch { }
    }

    static bool InitTsf()
    {
        try
        {
            TF_InputProcessorProfiles raw = new TF_InputProcessorProfiles();
            profiles = (ITfInputProcessorProfiles)raw;
            mgr = (ITfInputProcessorProfileMgr)raw;
            return true;
        }
        catch (Exception ex) { Log("创建 TSF 对象失败: " + ex.Message); return false; }
    }

    static string FormatTip(ushort langid, Guid clsid, Guid guid)
    {
        return string.Format(CultureInfo.InvariantCulture, "{0:X4}:{{{1}}}{{{2}}}",
            langid, clsid.ToString("D").ToUpperInvariant(), guid.ToString("D").ToUpperInvariant());
    }

    // 把 TIP 解析成输入法的显示名（如"微软拼音"），失败则退回 TIP 字符串。
    // 菜单里显示长 TIP 会把菜单撑得过宽，所以统一用显示名。结果做缓存，避免每次开菜单都调 COM。
    static readonly Dictionary<string, string> tipNameCache = new Dictionary<string, string>();

    static string TipName(ushort langid, Guid clsid, Guid guid)
    {
        string tip = FormatTip(langid, clsid, guid);
        string cached;
        if (tipNameCache.TryGetValue(tip, out cached)) return cached;
        string name = tip;
        try
        {
            Guid c = clsid, g = guid;
            IntPtr bstr;
            if (profiles.GetLanguageProfileDescription(ref c, langid, ref g, out bstr) == 0 && bstr != IntPtr.Zero)
            {
                string s = Marshal.PtrToStringBSTR(bstr);
                Marshal.FreeBSTR(bstr);
                if (!string.IsNullOrEmpty(s)) name = s;
            }
        }
        catch { }
        tipNameCache[tip] = name;
        return name;
    }

    static string GetActiveTip()
    {
        TF_INPUTPROCESSORPROFILE p = new TF_INPUTPROCESSORPROFILE();
        Guid cat = CAT_KEYBOARD;
        int hr = mgr.GetActiveProfile(ref cat, ref p);
        if (hr != 0) return "读取失败 hr=0x" + hr.ToString("X8");
        if (p.dwProfileType != TF_PROFILETYPE_INPUTPROCESSOR)
            return string.Format(CultureInfo.InvariantCulture, "{0:X4}:HKL:{1}", p.langid, p.hkl.ToString("X8"));
        return FormatTip(p.langid, p.clsid, p.guidProfile);
    }

    static int ActivateTip(ushort langid, Guid clsid, Guid profile)
    {
        Guid c = clsid, g = profile;
        try { profiles.ChangeCurrentLanguage(langid); }
        catch { }
        return mgr.ActivateProfile(TF_PROFILETYPE_INPUTPROCESSOR, langid, ref c, ref g, IntPtr.Zero, FlagMode);
    }

    static bool ParseTip(string tip, out ushort langid, out Guid clsid, out Guid profile)
    {
        langid = 0; clsid = Guid.Empty; profile = Guid.Empty;
        if (tip == null) return false;
        int colon = tip.IndexOf(':');
        if (colon != 4) return false;
        if (!ushort.TryParse(tip.Substring(0, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out langid)) return false;
        int a = tip.IndexOf('{', colon), b = tip.IndexOf('}', a), c = tip.IndexOf('{', b), d = tip.IndexOf('}', c);
        if (a < 0 || b < 0 || c < 0 || d < 0) return false;
        try
        {
            clsid = new Guid(tip.Substring(a + 1, b - a - 1));
            profile = new Guid(tip.Substring(c + 1, d - c - 1));
        }
        catch { return false; }
        return true;
    }

    static List<Rule> LoadRules()
    {
        List<Rule> list = new List<Rule>();
        string path = Path.Combine(dir, "rules.txt");
        if (!File.Exists(path))
        {
            File.WriteAllText(path,
                "# ImeBind 规则：每行 \"程序名 = 输入法TIP\"，程序名不区分大小写\r\n" +
                "# 本机可用的 TIP 用 ImeBind.exe --list 查看\r\n" +
                "# 示例（微软拼音）：\r\n" +
                "# dota2.exe = 0804:{81D4E9C9-1D3B-41BC-9E6C-4B40BF79E35E}{FA550B04-5AD7-411F-A5AC-CA038EC515D7}\r\n",
                Encoding.UTF8);
        }
        foreach (string raw in File.ReadAllLines(path))
        {
            string line = raw.Trim();
            if (line.Length == 0 || line.StartsWith("#")) continue;
            int eq = line.IndexOf('=');
            if (eq <= 0) continue;
            Rule r = new Rule();
            r.Exe = line.Substring(0, eq).Trim().ToLowerInvariant();
            r.Tip = line.Substring(eq + 1).Trim().ToUpperInvariant();
            if (!ParseTip(r.Tip, out r.Langid, out r.Clsid, out r.Profile))
            {
                Log("规则解析失败，已跳过: " + line);
                continue;
            }
            if (r.Exe.Length == 0) continue;
            list.Add(r);
        }
        return list;
    }

    static Rule FindRule(List<Rule> rules, string exe)
    {
        if (string.IsNullOrEmpty(exe)) return null;
        string e = exe.ToLowerInvariant();
        foreach (Rule r in rules) if (r.Exe == e) return r;
        return null;
    }

    static uint lastPid;
    static IntPtr lastHwnd;
    static string lastExe = "";

    // 按窗口句柄+进程号做缓存；句柄或进程变化就重新解析（避免 PID 复用导致的名字错位）
    static string GetForegroundExe()
    {
        IntPtr hWnd = Native.GetForegroundWindow();
        uint pid;
        Native.GetWindowThreadProcessId(hWnd, out pid);
        if (pid == 0) { lastPid = 0; lastHwnd = IntPtr.Zero; lastExe = ""; return ""; }
        if (pid == lastPid && hWnd == lastHwnd) return lastExe;

        string name = "";
        IntPtr h = Native.OpenProcess(0x1000 /*PROCESS_QUERY_LIMITED_INFORMATION*/, false, pid);
        if (h != IntPtr.Zero)
        {
            try
            {
                uint size = 1024;
                StringBuilder sb = new StringBuilder(1024);
                if (Native.QueryFullProcessImageName(h, 0, sb, ref size)) name = sb.ToString();
            }
            finally { Native.CloseHandle(h); }
        }
        if (name.Length > 0)
        {
            int i = name.LastIndexOf('\\');
            name = (i >= 0) ? name.Substring(i + 1) : name;
        }
        lastPid = pid; lastHwnd = hWnd; lastExe = name;
        return name;
    }

    static void ListProfiles()
    {
        IntPtr pLang;
        uint count;
        int hr = profiles.GetLanguageList(out pLang, out count);
        if (hr != 0) { Log("GetLanguageList 失败 hr=0x" + hr.ToString("X8")); return; }

        string active = GetActiveTip();
        HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        Log("本机已安装的键盘输入法（把 TIP 字符串填进 rules.txt）：");
        for (uint i = 0; i < count; i++)
        {
            ushort langid = (ushort)Marshal.ReadInt16(pLang, (int)(i * 2));
            IEnumTfInputProcessorProfiles en;
            if (mgr.EnumProfiles(langid, out en) != 0 || en == null) continue;
            try
            {
                TF_INPUTPROCESSORPROFILE[] one = new TF_INPUTPROCESSORPROFILE[1];
                while (true)
                {
                    uint fetched;
                    if (en.Next(1, one, out fetched) != 0 || fetched == 0) break;
                    TF_INPUTPROCESSORPROFILE pr = one[0];
                    // 只要键盘类输入法：否则会把触控输入、手写、语音识别等一并列出来
                    if (pr.dwProfileType != TF_PROFILETYPE_INPUTPROCESSOR) continue;
                    if (pr.catid != CAT_KEYBOARD) continue;
                    string tip = FormatTip(pr.langid, pr.clsid, pr.guidProfile);
                    if (!seen.Add(tip)) continue;                       // 0x0000 语言会重复枚举
                    string name = "";
                    IntPtr bstr;
                    Guid c = pr.clsid, g = pr.guidProfile;
                    if (profiles.GetLanguageProfileDescription(ref c, langid, ref g, out bstr) == 0 && bstr != IntPtr.Zero)
                    {
                        name = Marshal.PtrToStringBSTR(bstr);
                        Marshal.FreeBSTR(bstr);
                    }
                    Log("  " + tip + "   " + name + (string.Equals(tip, active, StringComparison.OrdinalIgnoreCase) ? "   ← 当前" : ""));
                }
            }
            finally { Marshal.ReleaseComObject(en); }
        }
        Native.CoTaskMemFree(pLang);
    }

    // ── 切换逻辑 ─────────────────────────────────────────────────────────────

    static void RestoreForced()
    {
        if (forcedTip == null) return;
        ushort langid; Guid clsid; Guid prof;
        if (ParseTip(forcedTip, out langid, out clsid, out prof))
        {
            int hr = ActivateTip(langid, clsid, prof);
            Log(string.Format(CultureInfo.InvariantCulture, "还原为 {0} (hr=0x{1:X8})", forcedTip, hr));
        }
        forcedTip = null;
    }

    static void Poll()
    {
        if (paused) return;
        try
        {
            string exe = GetForegroundExe();
            if (exe != seenExe)
            {
                Log("前台切换: " + seenExe + " -> " + exe);
                Rule r = FindRule(rules, exe);
                if (r != null)
                {
                    string cur = GetActiveTip();
                    if (!string.Equals(cur, r.Tip, StringComparison.OrdinalIgnoreCase))
                    {
                        forcedTip = cur;
                        int hr = ActivateTip(r.Langid, r.Clsid, r.Profile);
                        Log(string.Format(CultureInfo.InvariantCulture, "应用规则 {0}: {1} -> {2} (hr=0x{3:X8})", r.Exe, cur, r.Tip, hr));
                    }
                    reassertUntil = DateTime.Now.AddSeconds(1.5);
                }
                else if (forcedTip != null)
                {
                    Log("离开规则程序，开始还原");
                    RestoreForced();
                }
                seenExe = exe;
                UpdateTrayText();
            }
            else if (reassertUntil > DateTime.Now)
            {
                Rule r = FindRule(rules, exe);
                if (r != null)
                {
                    string cur = GetActiveTip();
                    if (!string.Equals(cur, r.Tip, StringComparison.OrdinalIgnoreCase))
                    {
                        int hr = ActivateTip(r.Langid, r.Clsid, r.Profile);
                        Log(string.Format(CultureInfo.InvariantCulture, "补偿一次 {0}: {1} -> {2} (hr=0x{3:X8})", r.Exe, cur, r.Tip, hr));
                        reassertUntil = DateTime.MinValue;
                    }
                }
            }
        }
        catch (Exception ex) { Log("异常: " + ex.Message); }
    }

    // ── 托盘 ────────────────────────────────────────────────────────────────

    static Icon LoadTrayIcon(out int pickedSize)
    {
        string p = Path.Combine(dir, "icon.ico");
        int target = 16;
        try { target = (int)Math.Round(16.0 * Native.GetDpiForSystem() / 96.0); } catch { }
        if (target < 16) target = 16;
        pickedSize = target;
        if (File.Exists(p))
        {
            try { return new Icon(p, target, target); } catch { }
        }
        try { return Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
        return SystemIcons.Application;
    }

    static string ShortStatus()
    {
        string s = "ImeBind " + (paused ? "[已暂停]" : "[运行中]") + " 规则" + rules.Count;
        string fg = lastExe;
        if (!string.IsNullOrEmpty(fg)) s += " 前台:" + fg;
        if (s.Length > 62) s = s.Substring(0, 62);
        return s;
    }

    static void UpdateTrayText()
    {
        if (tray == null) return;
        string s = ShortStatus();
        if (s == lastTrayText) return;          // 只在变化时调 shell
        try { tray.Text = s; lastTrayText = s; } catch { }
    }

    static void OpenPath(string path)
    {
        try { Process.Start(path); }
        catch (Exception ex) { Log("打开失败 " + path + " : " + ex.Message); }
    }

    static void ReloadRules()
    {
        rules = LoadRules();
        Log("重新加载 rules.txt：" + rules.Count + " 条规则");
        lastTrayText = "";
        UpdateTrayText();
    }

    static void TogglePause()
    {
        paused = !paused;
        if (paused) RestoreForced();
        Log(paused ? "已暂停自动切换" : "已恢复自动切换");
        lastTrayText = "";
        UpdateTrayText();
    }

    static void ExitApp()
    {
        try { RestoreForced(); } catch { }
        if (tray != null) { tray.Visible = false; tray.Dispose(); tray = null; }
        Log("退出");
        Application.Exit();
    }

    static void BuildMenu()
    {
        menu.Items.Clear();

        ToolStripMenuItem st = new ToolStripMenuItem("状态：" + (paused ? "已暂停" : "运行中"));
        st.Enabled = false;
        menu.Items.Add(st);

        string fg = GetForegroundExe();
        ToolStripMenuItem fe = new ToolStripMenuItem("前台程序：" + (string.IsNullOrEmpty(fg) ? "(无)" : fg));
        fe.Enabled = false;
        menu.Items.Add(fe);

        menu.Items.Add(new ToolStripSeparator());
        if (rules.Count == 0)
        {
            ToolStripMenuItem none = new ToolStripMenuItem("（rules.txt 里没有有效规则）");
            none.Enabled = false;
            menu.Items.Add(none);
        }
        else
        {
            ToolStripMenuItem head = new ToolStripMenuItem("已生效的规则");
            head.Enabled = false;
            menu.Items.Add(head);
            foreach (Rule r in rules)
            {
                ToolStripMenuItem it = new ToolStripMenuItem("  " + r.Exe + " → " + TipName(r.Langid, r.Clsid, r.Profile));
                it.Enabled = false;
                menu.Items.Add(it);
            }
        }

        menu.Items.Add(new ToolStripSeparator());
        ToolStripMenuItem pause = new ToolStripMenuItem(paused ? "恢复自动切换" : "暂停自动切换");
        pause.Click += delegate { TogglePause(); };
        menu.Items.Add(pause);

        ToolStripMenuItem reload = new ToolStripMenuItem("重新加载 rules.txt");
        reload.Click += delegate { ReloadRules(); };
        menu.Items.Add(reload);

        menu.Items.Add(new ToolStripSeparator());
        ToolStripMenuItem er = new ToolStripMenuItem("编辑 rules.txt");
        er.Click += delegate { OpenPath(Path.Combine(dir, "rules.txt")); };
        menu.Items.Add(er);

        ToolStripMenuItem el = new ToolStripMenuItem("查看日志");
        el.Click += delegate { OpenPath(logPath); };
        menu.Items.Add(el);

        ToolStripMenuItem od = new ToolStripMenuItem("打开程序所在文件夹");
        od.Click += delegate { OpenPath(dir); };
        menu.Items.Add(od);

        menu.Items.Add(new ToolStripSeparator());
        ToolStripMenuItem quit = new ToolStripMenuItem("退出");
        quit.Click += delegate { ExitApp(); };
        menu.Items.Add(quit);
    }

    static bool SetupTray()
    {
        try
        {
            menu = new ContextMenuStrip();
            menu.Opening += delegate { BuildMenu(); };

            int picked;
            tray = new NotifyIcon();
            tray.Icon = LoadTrayIcon(out picked);
            tray.ContextMenuStrip = menu;
            tray.Text = ShortStatus();
            lastTrayText = tray.Text;
            tray.DoubleClick += delegate
            {
                try { tray.ShowBalloonTip(4000, "ImeBind", ShortStatus() + "\r\n右键图标可打开菜单", ToolTipIcon.Info); }
                catch { }
            };
            tray.Visible = true;
            Log("托盘图标已创建（取 " + picked + "px 帧）");
            return true;
        }
        catch (Exception ex)
        {
            Log("创建托盘图标失败: " + ex.Message);
            if (tray != null) { try { tray.Dispose(); } catch { } tray = null; }
            return false;
        }
    }

    [STAThread]
    static void Main(string[] args)
    {
        dir = Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location);
        logPath = Path.Combine(dir, "imebind.log");

        // 先声明 DPI 感知（PerMonitorV2，失败退回旧 API），必须在创建任何窗口之前调用。
        // 否则进程被视为 DPI 无感知，GetDpiForSystem 恒为 96，托盘会拿到 16px 帧再被系统拉伸。
        try
        {
            if (!Native.SetProcessDpiAwarenessContext(new IntPtr(-4 /*PER_MONITOR_AWARE_V2*/)))
                Native.SetProcessDPIAware();
        }
        catch { try { Native.SetProcessDPIAware(); } catch { } }

        // 从终端启动时把输出接到父控制台，否则 winexe 的诊断信息用户看不到。
        // 用控制台自身的编码写（通常 cp936），否则中文在控制台里是乱码。
        if (Native.AttachConsole(-1))
        {
            try
            {
                StreamWriter sw = new StreamWriter(Console.OpenStandardOutput(), Console.OutputEncoding);
                sw.AutoFlush = true;
                Console.SetOut(sw);
                hasConsole = true;
            }
            catch { }
        }

        string mode = (args.Length > 0) ? args[0].ToLowerInvariant() : "";
        bool showTray = true;
        for (int i = 0; i < args.Length; i++)
        {
            if (string.Equals(args[i], "--no-tray", StringComparison.OrdinalIgnoreCase)) showTray = false;
        }

        // 单实例只约束"常驻模式"：两个常驻实例会互相打架。
        // 诊断模式（--list/--status/--activate）必须能在常驻实例运行时照常使用。
        IntPtr mutex = Native.CreateMutex(IntPtr.Zero, false, "ImeBind_SingleInstance");
        bool alreadyRunning = (Marshal.GetLastWin32Error() == ERROR_ALREADY_EXISTS);

        if (!InitTsf())
        {
            Console.WriteLine("初始化 TSF 失败。");
            return;
        }

        if (mode == "--list" || mode == "-l")
        {
            ListProfiles();
            return;
        }
        if (mode == "--status" || mode == "--probe")
        {
            Log("前台程序: " + GetForegroundExe());
            Log("当前输入法(本进程视角): " + GetActiveTip());
            Log("日志文件: " + logPath);
            return;
        }
        if (mode == "--menu-dump")
        {
            // 自检：把托盘菜单构建一遍并打印内容，用来在不点鼠标的情况下验证菜单逻辑
            rules = LoadRules();
            menu = new ContextMenuStrip();
            try { BuildMenu(); }
            catch (Exception ex) { Log("构建托盘菜单时异常: " + ex.Message); return; }
            Log("托盘菜单共 " + menu.Items.Count + " 项（含分隔线）：");
            foreach (ToolStripItem it in menu.Items)
            {
                if (it is ToolStripSeparator) { Log("  ---"); continue; }
                Log("  " + (it.Enabled ? "[可点击] " : "[只读]   ") + it.Text);
            }
            return;
        }
        if (mode == "--activate")
        {
            if (args.Length < 2) { Log("用法: ImeBind.exe --activate <TIP> [both|session|process]"); return; }
            if (args.Length > 2)
            {
                string f = args[2].ToLowerInvariant();
                if (f == "session") FlagMode = TF_IPPMF_FORSESSION;
                else if (f == "process") FlagMode = TF_IPPMF_FORPROCESS;
            }
            ushort langid; Guid clsid; Guid prof;
            if (!ParseTip(args[1], out langid, out clsid, out prof)) { Log("TIP 解析失败: " + args[1]); return; }
            int hr = ActivateTip(langid, clsid, prof);
            Thread.Sleep(300);
            Log(string.Format(CultureInfo.InvariantCulture, "已激活 {0} (hr=0x{1:X8})，当前为 {2}", args[1], hr, GetActiveTip()));
            return;
        }

        if (alreadyRunning)
        {
            Log("已有 ImeBind 实例在运行，本次退出。");
            return;
        }

        rules = LoadRules();
        if (rules.Count == 0)
        {
            Log("rules.txt 里没有有效规则，退出。用 ImeBind.exe --list 查看可用的输入法 TIP。");
            return;
        }
        Log("启动：加载 " + rules.Count + " 条规则，当前输入法=" + GetActiveTip());
        foreach (Rule r in rules) Log("  规则 " + r.Exe + " -> " + r.Tip);

        if (showTray && SetupTray())
        {
            // 有托盘时用 WinForms 消息循环，顺便让菜单可用
            System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer();
            timer.Interval = 250;
            timer.Tick += delegate { Poll(); };
            timer.Start();
            Application.Run();
        }
        else
        {
            if (showTray) Log("托盘不可用，转为无界面模式继续运行");
            while (true)
            {
                Poll();
                Thread.Sleep(250);
            }
        }
    }
}

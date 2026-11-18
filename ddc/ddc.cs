// ============================================================================
// ddc.exe —— AOC AG275UXM2 输入源切换（Windows 标准 DDC/CI 控制台工具）
//
// 用途: 经 DP1 接口的 DDC 信号通路（Windows MCCS / dxva2 Physical Monitor API）
//       切换显示器输入源: HDMI1 / HDMI2 / DP1 / DP2。全程只用标准 DDC/CI 命令
//       （VCP 0x60），不依赖显示器 USB 上行线、不加载 RhHidAPI/EneEc 等厂商 DLL、
//       不需要 G-Menu 运行。
//
// 逆向依据（G-Menu 反汇编）:
//   - 输入源 VCP: 0x60（OP_60_InputSource，见 Zeasn.Equipment.Entity.Lib.decompiled.cs:10679）
//   - 输入源编码（StandardSource_E，同上 :11063）:
//       DP1=15(0x0F) DP2=16(0x10) HDMI1=17(0x11) HDMI2=18(0x12)
//       HDMI3=19 USB_C1=21(0x15) USB_C2=22(0x16)
//   - G-Menu 通过显示器 USB 桥（RTK HID / ENE WinUSB 私有通道）下发 DDC/CI 帧:
//       Set VCP 0x60: 6E 51 84 03 60 00 <code> <XOR>
//       Get VCP 0x60: 6E 51 82 01 60 <XOR>
//     本工具改用 Windows 官方 DDC/CI 入口（dxva2 物理监视器 API），由显示驱动
//     沿 DP1 链路的 DDC 通道下发“同一 VCP 语义、同一数值”，因此帧格式仅作
//     离线校验（ddc.exe test），运行时并不直接拼发原始帧。
//
// 编译（Windows 自带 .NET Framework 4.x csc，无需 SDK）:
//   build.bat            （或 powershell -ExecutionPolicy Bypass -File build.ps1）
//   等价命令:
//   C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe /nologo /target:exe
//       /platform:anycpu /optimize+ /codepage:65001 /out:ddc.exe ddc.cs
//
// 退出码: 0=成功 1=用法错误 2=未找到显示器 3=DDC 读写失败 4=自检失败
// ============================================================================

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace AocDdc
{
    internal static class NativeMethods
    {
        [StructLayout(LayoutKind.Sequential)]
        public struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct MONITORINFOEX
        {
            public int cbSize;
            public RECT rcMonitor;
            public RECT rcWork;
            public uint dwFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
            public string szDevice;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct DISPLAY_DEVICE
        {
            public int cb;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
            public string DeviceName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            public string DeviceString;
            public uint StateFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            public string DeviceID;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            public string DeviceKey;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct PHYSICAL_MONITOR
        {
            public IntPtr hPhysicalMonitor;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            public string szPhysicalMonitorDescription;
        }

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        public delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdcMonitor, ref RECT lprcMonitor, IntPtr dwData);

        // ---------------- user32 ----------------

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr lprcClip, MonitorEnumProc lpfnEnum, IntPtr dwData);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFOEX lpmi);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern bool EnumDisplayDevices(string lpDevice, uint iDevNum, ref DISPLAY_DEVICE lpDisplayDevice, uint dwFlags);

        // ---------------- dxva2（MCCS / 物理监视器 DDC/CI） ----------------

        [DllImport("dxva2.dll", SetLastError = true)]
        public static extern bool GetNumberOfPhysicalMonitorsFromHMONITOR(IntPtr hMonitor, ref uint pdwNumberOfPhysicalMonitors);

        [DllImport("dxva2.dll", SetLastError = true)]
        public static extern bool GetPhysicalMonitorsFromHMONITOR(IntPtr hMonitor, uint dwPhysicalMonitorArraySize, [Out] PHYSICAL_MONITOR[] pPhysicalMonitorArray);

        [DllImport("dxva2.dll", SetLastError = true)]
        public static extern bool DestroyPhysicalMonitors(uint dwPhysicalMonitorArraySize, [In] PHYSICAL_MONITOR[] pPhysicalMonitorArray);

        [DllImport("dxva2.dll", SetLastError = true)]
        public static extern bool GetMonitorCapabilities(IntPtr hMonitor, ref uint pdwMonitorCapabilities, ref uint pdwSupportedColorTemperatures);

        [DllImport("dxva2.dll", SetLastError = true)]
        public static extern bool GetVCPFeatureAndVCPFeatureReply(IntPtr hMonitor, byte bVCPCode, IntPtr pvct, ref uint pdwCurrentValue, ref uint pdwMaximumValue);

        [DllImport("dxva2.dll", SetLastError = true)]
        public static extern bool SetVCPFeature(IntPtr hMonitor, byte bVCPCode, uint dwNewValue);
    }

    internal sealed class MonitorEntry
    {
        public IntPtr HMonitor;
        public string DeviceName;
        public string Model;
        public string MonitorId;
        public bool IsAoc;
        public NativeMethods.PHYSICAL_MONITOR[] Physicals;
        public int LastError;
    }

    internal static class Program
    {
        // VCP 0x60 = 输入源（G-Menu: OP_60_InputSource）
        private const byte VcpInputSource = 0x60;

        // 输入源编码（G-Menu: StandardSource_E）
        private const uint SrcVgaAnalog = 0x01;
        private const uint SrcDvi1 = 0x03;
        private const uint SrcDp1 = 0x0F;
        private const uint SrcDp2 = 0x10;
        private const uint SrcHdmi1 = 0x11;
        private const uint SrcHdmi2 = 0x12;
        private const uint SrcHdmi3 = 0x13;
        private const uint SrcUsbC1 = 0x15;
        private const uint SrcUsbC2 = 0x16;

        private static int Main(string[] args)
        {
            int monitorIndex = 0;
            List<string> positional = new List<string>();

            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i];

                if (a.Equals("--monitor", StringComparison.OrdinalIgnoreCase) ||
                    a.Equals("-m", StringComparison.OrdinalIgnoreCase))
                {
                    if (i + 1 >= args.Length)
                    {
                        Console.Error.WriteLine("[错误] --monitor 需要一个序号参数（从 1 开始）");
                        return 1;
                    }
                    string v = args[++i];
                    if (!int.TryParse(v, out monitorIndex) || monitorIndex < 1)
                    {
                        Console.Error.WriteLine("[错误] 无效的 --monitor 值: " + v);
                        return 1;
                    }
                    continue;
                }

                if (a.StartsWith("--monitor=", StringComparison.OrdinalIgnoreCase))
                {
                    string v = a.Substring("--monitor=".Length);
                    if (!int.TryParse(v, out monitorIndex) || monitorIndex < 1)
                    {
                        Console.Error.WriteLine("[错误] 无效的 --monitor 值: " + v);
                        return 1;
                    }
                    continue;
                }

                positional.Add(a);
            }

            if (positional.Count == 0)
            {
                PrintHelp();
                return 1;
            }

            string cmd = positional[0].ToLowerInvariant();
            switch (cmd)
            {
                case "help":
                case "-h":
                case "--help":
                case "/?":
                    PrintHelp();
                    return 0;
                case "test":
                    return RunSelfTest();
                case "list":
                    return RunList();
                case "diag":
                    return RunDiag(monitorIndex);
                case "watch":
                    return RunWatch(monitorIndex);
                case "query":
                    return RunQuery(monitorIndex);
                case "dp1":
                    return RunSwitch(monitorIndex, SrcDp1);
                case "dp2":
                    return RunSwitch(monitorIndex, SrcDp2);
                case "hdmi1":
                    return RunSwitch(monitorIndex, SrcHdmi1);
                case "hdmi2":
                    return RunSwitch(monitorIndex, SrcHdmi2);
                default:
                    Console.Error.WriteLine("[错误] 未知命令: " + cmd);
                    PrintHelp();
                    return 1;
            }
        }

        // ------------------------------------------------------------------
        // 帮助
        // ------------------------------------------------------------------
        private static void PrintHelp()
        {
            Console.WriteLine("ddc.exe —— AOC AG275UXM2 输入源切换（Windows 标准 DDC/CI，经 DP1 的 DDC 信号通路）");
            Console.WriteLine("版本 1.2.0  （VCP 0x60 输入源协议取自 G-Menu 反汇编；传输层为 Windows MCCS）");
            Console.WriteLine();
            Console.WriteLine("用法:");
            Console.WriteLine("  ddc.exe dp1    [--monitor N]    切换到 DP1");
            Console.WriteLine("  ddc.exe dp2    [--monitor N]    切换到 DP2");
            Console.WriteLine("  ddc.exe hdmi1  [--monitor N]    切换到 HDMI1");
            Console.WriteLine("  ddc.exe hdmi2  [--monitor N]    切换到 HDMI2");
            Console.WriteLine("  ddc.exe query  [--monitor N]    读取当前输入源（VCP 0x60）");
            Console.WriteLine("  ddc.exe list                    列出显示器及物理监视器");
            Console.WriteLine("  ddc.exe diag   [--monitor N]    DDC 诊断（MCCS 能力位 + VCP 读取）");
            Console.WriteLine("  ddc.exe watch  [--monitor N]    持续监视输入源（每 0.5s 回读 VCP 0x60，Ctrl+C 退出）");
            Console.WriteLine("  ddc.exe test                    离线自检（校验 G-Menu DDC 帧/输入源编码）");
            Console.WriteLine();
            Console.WriteLine("选项:");
            Console.WriteLine("  --monitor N, -m N   指定第 N 个显示器（从 1 开始；默认自动选择 AOC 显示器）");
            Console.WriteLine();
            Console.WriteLine("说明:");
            Console.WriteLine("  * 本工具只发标准 DDC/CI MCCS 命令（VCP 0x60），沿显示器与 PC 之间");
            Console.WriteLine("    （DP1）的连接链路下发；不依赖显示器 USB 上行线，不加载任何厂商 DLL，");
            Console.WriteLine("    也无需运行 G-Menu。");
            Console.WriteLine("  * 显示器需在 OSD 中开启 DDC/CI（AOC：设置 → DDC/CI → 开）。");
            Console.WriteLine("  * 切走当前输入后，本输入口的 DDC 通常会暂时失效（显示器已不在该输入上），");
            Console.WriteLine("    这是正常现象；切回该输入后自动恢复。");
            Console.WriteLine("  * 多显示器时先运行 list 查看编号，再用 --monitor N 指定目标。");
            Console.WriteLine();
            Console.WriteLine("退出码: 0=成功 1=用法错误 2=未找到显示器 3=DDC 读写失败 4=自检失败");
        }

        // ------------------------------------------------------------------
        // 显示器枚举 / 选择 / 物理监视器句柄
        // ------------------------------------------------------------------
        private static List<MonitorEntry> EnumerateDisplays()
        {
            List<MonitorEntry> list = new List<MonitorEntry>();

            NativeMethods.MonitorEnumProc proc = delegate(IntPtr hMonitor, IntPtr hdc, ref NativeMethods.RECT lprc, IntPtr data)
            {
                NativeMethods.MONITORINFOEX mi = new NativeMethods.MONITORINFOEX();
                mi.cbSize = Marshal.SizeOf(typeof(NativeMethods.MONITORINFOEX));
                if (NativeMethods.GetMonitorInfo(hMonitor, ref mi))
                {
                    MonitorEntry entry = new MonitorEntry();
                    entry.HMonitor = hMonitor;
                    entry.DeviceName = mi.szDevice == null ? string.Empty : mi.szDevice.Trim();

                    string model;
                    string monitorId;
                    GetMonitorIdentity(entry.DeviceName, out model, out monitorId);
                    entry.Model = model;
                    entry.MonitorId = monitorId;
                    entry.IsAoc = LooksLikeAoc(entry);
                    list.Add(entry);
                }
                return true;
            };

            NativeMethods.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, proc, IntPtr.Zero);
            GC.KeepAlive(proc);

            list.Sort(delegate(MonitorEntry a, MonitorEntry b)
            {
                int ka = DisplaySortKey(a.DeviceName);
                int kb = DisplaySortKey(b.DeviceName);
                if (ka != kb)
                {
                    return ka.CompareTo(kb);
                }
                return string.CompareOrdinal(a.DeviceName, b.DeviceName);
            });

            return list;
        }

        private static int DisplaySortKey(string deviceName)
        {
            int num = 0;
            int mul = 1;
            bool any = false;
            for (int i = deviceName.Length - 1; i >= 0; i--)
            {
                char c = deviceName[i];
                if (c >= '0' && c <= '9')
                {
                    num += (c - '0') * mul;
                    mul *= 10;
                    any = true;
                }
                else if (any)
                {
                    break;
                }
            }
            return any ? num : int.MaxValue;
        }

        private static void GetMonitorIdentity(string deviceName, out string model, out string monitorId)
        {
            model = string.Empty;
            monitorId = string.Empty;
            try
            {
                NativeMethods.DISPLAY_DEVICE dd = new NativeMethods.DISPLAY_DEVICE();
                dd.cb = Marshal.SizeOf(typeof(NativeMethods.DISPLAY_DEVICE));
                if (NativeMethods.EnumDisplayDevices(deviceName, 0, ref dd, 0))
                {
                    if (dd.DeviceString != null)
                    {
                        model = dd.DeviceString.Trim();
                    }
                    if (dd.DeviceID != null)
                    {
                        monitorId = dd.DeviceID.Trim();
                    }
                }
            }
            catch (Exception)
            {
                // 忽略：型号仅用于展示与自动选择
            }
        }

        private static bool LooksLikeAoc(MonitorEntry m)
        {
            string s = (m.Model == null ? string.Empty : m.Model) + " " + (m.MonitorId == null ? string.Empty : m.MonitorId);
            s = s.ToUpperInvariant();
            return s.Contains("AOC") || s.Contains("AGON") || s.Contains("AG27");
        }

        private static MonitorEntry SelectMonitor(int forcedIndex, out int errorCode)
        {
            errorCode = 0;
            List<MonitorEntry> list = EnumerateDisplays();

            if (list.Count == 0)
            {
                Console.Error.WriteLine("[错误] 未检测到任何显示器（EnumDisplayMonitors 返回 0）。");
                Console.Error.WriteLine("       请确认显示器已开启并连接到本机，且 Windows「显示设置」中能看到该显示器。");
                errorCode = 2;
                return null;
            }

            if (forcedIndex >= 1)
            {
                if (forcedIndex > list.Count)
                {
                    Console.Error.WriteLine("[错误] --monitor {0} 超出范围：本机共 {1} 个显示器。", forcedIndex, list.Count);
                    errorCode = 2;
                    return null;
                }
                return list[forcedIndex - 1];
            }

            for (int i = 0; i < list.Count; i++)
            {
                if (list[i].IsAoc)
                {
                    return list[i];
                }
            }

            if (list.Count == 1)
            {
                return list[0];
            }

            Console.Error.WriteLine("[错误] 检测到 {0} 个显示器，但未能自动识别 AOC 显示器：", list.Count);
            for (int i = 0; i < list.Count; i++)
            {
                Console.Error.WriteLine("       [{0}] {1}  {2}", i + 1, list[i].DeviceName,
                    list[i].Model.Length == 0 ? "(未知型号)" : list[i].Model);
            }
            Console.Error.WriteLine("       请用 --monitor N 指定目标显示器（先用 list 查看编号）。");
            errorCode = 2;
            return null;
        }

        // 候选显示器列表：--monitor 指定时只取该序号；否则 AOC 优先，其余随后
        private static List<MonitorEntry> BuildCandidates(int forcedIndex, out int errorCode)
        {
            errorCode = 0;
            List<MonitorEntry> all = EnumerateDisplays();

            if (all.Count == 0)
            {
                Console.Error.WriteLine("[错误] 未检测到任何显示器（EnumDisplayMonitors 返回 0）。");
                Console.Error.WriteLine("       请确认显示器已开启并连接到本机，且 Windows「显示设置」中能看到该显示器。");
                errorCode = 2;
                return null;
            }

            if (forcedIndex >= 1)
            {
                if (forcedIndex > all.Count)
                {
                    Console.Error.WriteLine("[错误] --monitor {0} 超出范围：本机共 {1} 个显示器。", forcedIndex, all.Count);
                    errorCode = 2;
                    return null;
                }
                List<MonitorEntry> one = new List<MonitorEntry>();
                one.Add(all[forcedIndex - 1]);
                return one;
            }

            List<MonitorEntry> candidates = new List<MonitorEntry>();
            for (int i = 0; i < all.Count; i++)
            {
                if (all[i].IsAoc)
                {
                    candidates.Add(all[i]);
                }
            }
            for (int i = 0; i < all.Count; i++)
            {
                if (!all[i].IsAoc)
                {
                    candidates.Add(all[i]);
                }
            }
            Console.WriteLine("检测到 {0} 个显示器，将按顺序尝试：", all.Count);
            for (int i = 0; i < candidates.Count; i++)
            {
                Console.WriteLine("  [{0}] {1}  {2}", i + 1, candidates[i].DeviceName,
                    candidates[i].Model.Length > 0 ? candidates[i].Model : "(未知型号)");
            }
            Console.WriteLine();
            return candidates;
        }

        private static bool OpenPhysical(MonitorEntry entry)
        {
            if (entry.Physicals != null && entry.Physicals.Length > 0)
            {
                return true;
            }

            uint count = 0;
            if (!NativeMethods.GetNumberOfPhysicalMonitorsFromHMONITOR(entry.HMonitor, ref count) || count == 0)
            {
                entry.LastError = Marshal.GetLastWin32Error();
                return false;
            }

            NativeMethods.PHYSICAL_MONITOR[] arr = new NativeMethods.PHYSICAL_MONITOR[count];
            if (!NativeMethods.GetPhysicalMonitorsFromHMONITOR(entry.HMonitor, count, arr))
            {
                entry.LastError = Marshal.GetLastWin32Error();
                return false;
            }

            entry.Physicals = arr;
            return true;
        }

        private static void ClosePhysical(MonitorEntry entry)
        {
            if (entry.Physicals != null && entry.Physicals.Length > 0)
            {
                NativeMethods.DestroyPhysicalMonitors((uint)entry.Physicals.Length, entry.Physicals);
                entry.Physicals = null;
            }
        }

        // ------------------------------------------------------------------
        // DDC/CI 读写（MCCS VCP）
        // ------------------------------------------------------------------
        private static bool DdcRead(IntPtr hPhysical, byte vcp, out uint current, out uint maximum, out int win32Error)
        {
            current = 0;
            maximum = 0;
            bool ok = NativeMethods.GetVCPFeatureAndVCPFeatureReply(hPhysical, vcp, IntPtr.Zero, ref current, ref maximum);
            win32Error = ok ? 0 : Marshal.GetLastWin32Error();
            return ok;
        }

        private static bool DdcWrite(IntPtr hPhysical, byte vcp, uint value, out int win32Error)
        {
            bool ok = NativeMethods.SetVCPFeature(hPhysical, vcp, value);
            win32Error = ok ? 0 : Marshal.GetLastWin32Error();
            return ok;
        }

        // 带回退重试的读/写（DDC/CI 在输入切换前后偶尔会瞬时失败）
        private static bool DdcReadWait(IntPtr hPhysical, byte vcp, out uint current, out uint maximum, out int win32Error, int attempts)
        {
            current = 0;
            maximum = 0;
            win32Error = 0;
            for (int i = 0; i < attempts; i++)
            {
                if (DdcRead(hPhysical, vcp, out current, out maximum, out win32Error))
                {
                    return true;
                }
                if (i + 1 < attempts)
                {
                    Thread.Sleep(250);
                }
            }
            return false;
        }

        private static bool DdcWriteWait(IntPtr hPhysical, byte vcp, uint value, out int win32Error, int attempts)
        {
            win32Error = 0;
            for (int i = 0; i < attempts; i++)
            {
                if (DdcWrite(hPhysical, vcp, value, out win32Error))
                {
                    return true;
                }
                if (i + 1 < attempts)
                {
                    Thread.Sleep(300);
                }
            }
            return false;
        }

        // 写命令后轮询回读，确认显示器是否真的切换（0.25s × 20 ≈ 5s）
        private static int PollSource(IntPtr hPhysical, uint target, out uint lastValue, out bool sawTarget, out bool anyRead)
        {
            lastValue = 0;
            sawTarget = false;
            anyRead = false;
            for (int i = 0; i < 20; i++)
            {
                uint cur;
                uint max;
                int e;
                if (DdcRead(hPhysical, VcpInputSource, out cur, out max, out e))
                {
                    anyRead = true;
                    lastValue = cur;
                    if (cur == target)
                    {
                        sawTarget = true;
                        return i + 1;
                    }
                }
                Thread.Sleep(250);
            }
            return 0;
        }

        private static string WinErr(int code)
        {
            string message;
            try
            {
                message = new Win32Exception(code).Message;
            }
            catch (Exception)
            {
                message = "未知错误";
            }
            return string.Format("0x{0:X8} {1}", (uint)code, message);
        }

        private static string SourceName(uint code)
        {
            switch (code)
            {
                case SrcVgaAnalog:
                    return "VGA(D-SUB)";
                case SrcDvi1:
                    return "DVI1";
                case SrcDp1:
                    return "DP1";
                case SrcDp2:
                    return "DP2";
                case SrcHdmi1:
                    return "HDMI1";
                case SrcHdmi2:
                    return "HDMI2";
                case SrcHdmi3:
                    return "HDMI3";
                case SrcUsbC1:
                    return "USB-C1";
                case SrcUsbC2:
                    return "USB-C2";
                default:
                    return "未知输入源";
            }
        }

        // ------------------------------------------------------------------
        // 命令实现
        // ------------------------------------------------------------------
        private static int RunSwitch(int forcedIndex, uint source)
        {
            int ec;
            List<MonitorEntry> candidates = BuildCandidates(forcedIndex, out ec);
            if (candidates == null)
            {
                return ec;
            }

            bool anyOpened = false;
            bool anyWriteOk = false;
            uint lastSeen = 0xFFFFFFFF;
            string lastSeenName = string.Empty;

            for (int i = 0; i < candidates.Count; i++)
            {
                MonitorEntry m = candidates[i];
                Console.WriteLine("显示器: {0}  型号: {1}", m.DeviceName, m.Model.Length > 0 ? m.Model : "(未知)");

                if (!OpenPhysical(m))
                {
                    Console.Error.WriteLine("  [跳过] 无法打开物理监视器句柄 (Win32 {0})", WinErr(m.LastError));
                    continue;
                }
                anyOpened = true;

                try
                {
                    IntPtr h = m.Physicals[0].hPhysicalMonitor;

                    uint cur = 0;
                    uint max = 0;
                    int e;
                    bool readOk = DdcReadWait(h, VcpInputSource, out cur, out max, out e, 3);

                    if (readOk)
                    {
                        Console.WriteLine("  当前输入源: 0x{0:X2} ({1})", cur, SourceName(cur));
                        if (cur == source)
                        {
                            Console.WriteLine("[OK] 当前已处于 {0}，无需切换。", SourceName(source));
                            return 0;
                        }
                    }
                    else
                    {
                        Console.WriteLine("  当前输入源读取失败 (Win32 {0})，继续尝试。", WinErr(e));
                    }

                    bool writeOk = DdcWriteWait(h, VcpInputSource, source, out e, 3);
                    if (!writeOk)
                    {
                        Console.Error.WriteLine("  [失败] DDC 写入失败 (Win32 {0})", WinErr(e));
                        continue;
                    }
                    anyWriteOk = true;

                    Console.WriteLine("  [OK] 已发送切换命令，回读确认中 ...");
                    uint last = 0;
                    bool sawTarget;
                    bool anyRead;
                    int polls = PollSource(h, source, out last, out sawTarget, out anyRead);

                    if (sawTarget)
                    {
                        Console.WriteLine("[OK] 显示器已确认切换到 {0}（回读 VCP 0x60 = 0x{1:X2}，第 {2} 次回读命中）。", SourceName(source), source, polls);
                        return 0;
                    }

                    if (anyRead)
                    {
                        lastSeen = last;
                        lastSeenName = m.DeviceName;
                        Console.Error.WriteLine("  [WARN] 回读仍为 0x{0:X2} ({1})，未确认切换。", last, SourceName(last));
                    }
                    else
                    {
                        Console.WriteLine("  [WARN] 回读失败（显示器可能正在切换，或该链路 DDC 中断）。");
                    }
                }
                finally
                {
                    ClosePhysical(m);
                }
            }

            Console.WriteLine();
            if (anyWriteOk)
            {
                Console.Error.WriteLine("[WARN] 已向 {0} 个候选显示器发送切换命令，但均未回读确认。", candidates.Count);
                if (lastSeen != 0xFFFFFFFF)
                {
                    Console.Error.WriteLine("       最后回读: 0x{0:X2} ({1})（{2}）。", lastSeen, SourceName(lastSeen), lastSeenName);
                }
                Console.Error.WriteLine("       排查: 1) 显示器 OSD 中 DDC/CI 是否开启；2) 是否用 --monitor N 指定了正确显示器；");
                Console.Error.WriteLine("             3) 该显示器固件是否允许主机侧 VCP 0x60 切换（部分固件只读）。");
                return 0;
            }

            Console.Error.WriteLine("[错误] 所有候选显示器的 DDC 写入均失败。");
            Console.Error.WriteLine("       请检查显示器 OSD 的 DDC/CI 开关；必要时以管理员身份运行。");
            return anyOpened ? 3 : 2;
        }

        private static int RunQuery(int forcedIndex)
        {
            int ec;
            List<MonitorEntry> candidates = BuildCandidates(forcedIndex, out ec);
            if (candidates == null)
            {
                return ec;
            }

            for (int i = 0; i < candidates.Count; i++)
            {
                MonitorEntry m = candidates[i];
                Console.WriteLine("显示器: {0}  型号: {1}", m.DeviceName, m.Model.Length > 0 ? m.Model : "(未知)");

                if (!OpenPhysical(m))
                {
                    Console.Error.WriteLine("  [跳过] 无法打开物理监视器句柄 (Win32 {0})", WinErr(m.LastError));
                    continue;
                }

                try
                {
                    IntPtr h = m.Physicals[0].hPhysicalMonitor;
                    uint cur = 0;
                    uint max = 0;
                    int e;
                    if (!DdcReadWait(h, VcpInputSource, out cur, out max, out e, 3))
                    {
                        Console.Error.WriteLine("  [失败] 读取 VCP 0x60 (Win32 {0})", WinErr(e));
                        continue;
                    }

                    Console.WriteLine("VCP 0x60 (Input Source): 当前 = 0x{0:X2} ({1})，最大值 = 0x{2:X2}", cur, SourceName(cur), max);
                    return 0;
                }
                finally
                {
                    ClosePhysical(m);
                }
            }

            Console.Error.WriteLine("[错误] 所有候选显示器均无法读取 VCP 0x60。");
            return 3;
        }

        private static int RunList()
        {
            List<MonitorEntry> list = EnumerateDisplays();

            if (list.Count == 0)
            {
                Console.WriteLine("未检测到显示器。");
                return 2;
            }

            Console.WriteLine("显示器列表（共 {0} 个）:", list.Count);

            for (int i = 0; i < list.Count; i++)
            {
                MonitorEntry m = list[i];
                Console.WriteLine();
                Console.WriteLine("  [{0}] {1}{2}", i + 1, m.DeviceName, m.IsAoc ? "   <== AOC 目标显示器" : "");
                Console.WriteLine("      型号: {0}", m.Model.Length > 0 ? m.Model : "(未知)");
                Console.WriteLine("      ID:   {0}", m.MonitorId.Length > 0 ? m.MonitorId : "(无)");

                uint count = 0;
                if (NativeMethods.GetNumberOfPhysicalMonitorsFromHMONITOR(m.HMonitor, ref count) && count > 0)
                {
                    NativeMethods.PHYSICAL_MONITOR[] arr = new NativeMethods.PHYSICAL_MONITOR[count];
                    if (NativeMethods.GetPhysicalMonitorsFromHMONITOR(m.HMonitor, count, arr))
                    {
                        for (uint j = 0; j < count; j++)
                        {
                            Console.WriteLine("      物理监视器 {0}: {1}", j + 1,
                                arr[j].szPhysicalMonitorDescription == null ? "(无描述)" : arr[j].szPhysicalMonitorDescription);
                        }
                        NativeMethods.DestroyPhysicalMonitors(count, arr);
                    }
                    else
                    {
                        Console.WriteLine("      物理监视器: 枚举失败 (Win32 {0})", WinErr(Marshal.GetLastWin32Error()));
                    }
                }
                else
                {
                    Console.WriteLine("      物理监视器: 不可用（虚拟显示器/远程桌面，或显卡不支持 MCCS）");
                }
            }

            Console.WriteLine();
            Console.WriteLine("提示: 切换时如自动识别失败，可用 --monitor N 指定，例如: ddc.exe hdmi1 --monitor 2");
            return 0;
        }

        private static int RunDiag(int forcedIndex)
        {
            int ec;
            MonitorEntry m = SelectMonitor(forcedIndex, out ec);
            if (m == null)
            {
                return ec;
            }

            Console.WriteLine("显示器: {0}", m.DeviceName);
            Console.WriteLine("型号:   {0}", m.Model.Length > 0 ? m.Model : "(未知)");
            Console.WriteLine("ID:     {0}", m.MonitorId.Length > 0 ? m.MonitorId : "(无)");

            if (!OpenPhysical(m))
            {
                Console.Error.WriteLine("[错误] 无法打开物理监视器句柄 (Win32 {0})", WinErr(m.LastError));
                Console.Error.WriteLine("       该显示输出可能不支持 MCCS/DDC（虚拟显示器、远程桌面），或显示驱动未启用 DDC/CI。");
                return 3;
            }

            try
            {
                IntPtr h = m.Physicals[0].hPhysicalMonitor;
                Console.WriteLine("物理监视器: {0}", m.Physicals[0].szPhysicalMonitorDescription);

                uint caps = 0;
                uint temps = 0;
                bool capOk = NativeMethods.GetMonitorCapabilities(h, ref caps, ref temps);
                int capErr = capOk ? 0 : Marshal.GetLastWin32Error();

                if (capOk)
                {
                    Console.WriteLine("MCCS 能力位: 0x{0:X8}（非 0 通常表示支持 MCCS/DDC 控制）", caps);
                }
                else
                {
                    Console.WriteLine("MCCS 能力查询失败 (Win32 {0})", WinErr(capErr));
                    Console.WriteLine("       （该错误常见于显示器 EDID/驱动未声明 MCCS 版本；不影响 VCP 读写，可忽略）");
                }

                uint cur = 0;
                uint max = 0;
                int e;
                bool readOk = DdcRead(h, VcpInputSource, out cur, out max, out e);
                if (readOk)
                {
                    Console.WriteLine("VCP 0x60 读取成功: 当前输入源 = 0x{0:X2} ({1})", cur, SourceName(cur));
                }
                else
                {
                    Console.Error.WriteLine("VCP 0x60 读取失败 (Win32 {0})", WinErr(e));
                }

                if (!readOk && !capOk)
                {
                    Console.WriteLine("建议: 检查显示器 OSD 的 DDC/CI 开关；确认显示器当前输入与 PC 输入一致；确认显卡/显示器驱动支持 MCCS。");
                }

                return (readOk || capOk) ? 0 : 3;
            }
            finally
            {
                ClosePhysical(m);
            }
        }

        // ------------------------------------------------------------------
        // watch：持续监视输入源（排查用）
        // ------------------------------------------------------------------
        private static int RunWatch(int forcedIndex)
        {
            int ec;
            MonitorEntry m = SelectMonitor(forcedIndex, out ec);
            if (m == null)
            {
                return ec;
            }

            Console.WriteLine("显示器: {0}  型号: {1}", m.DeviceName, m.Model.Length > 0 ? m.Model : "(未知)");

            if (!OpenPhysical(m))
            {
                Console.Error.WriteLine("[错误] 无法打开物理监视器句柄 (Win32 {0})", WinErr(m.LastError));
                return 3;
            }

            bool stop = false;
            ConsoleCancelEventHandler handler = delegate(object sender, ConsoleCancelEventArgs args)
            {
                args.Cancel = true;
                stop = true;
            };
            Console.CancelKeyPress += handler;

            try
            {
                IntPtr h = m.Physicals[0].hPhysicalMonitor;
                uint last = 0xFFFFFFFF;
                Console.WriteLine("每 0.5 秒回读 VCP 0x60（输入源），按 Ctrl+C 退出。");

                while (!stop)
                {
                    uint cur;
                    uint max;
                    int e;
                    if (DdcRead(h, VcpInputSource, out cur, out max, out e))
                    {
                        string src = SourceName(cur);
                        if (cur != last)
                        {
                            Console.WriteLine("{0:HH:mm:ss}  输入源 = 0x{1:X2} ({2})  <== 变化", DateTime.Now, cur, src);
                            last = cur;
                        }
                        else
                        {
                            Console.WriteLine("{0:HH:mm:ss}  输入源 = 0x{1:X2} ({2})", DateTime.Now, cur, src);
                        }
                    }
                    else
                    {
                        Console.Error.WriteLine("{0:HH:mm:ss}  回读失败 (Win32 {1})", DateTime.Now, WinErr(e));
                    }

                    for (int k = 0; k < 5 && !stop; k++)
                    {
                        Thread.Sleep(100);
                    }
                }

                Console.WriteLine("已退出监视。");
                return 0;
            }
            finally
            {
                Console.CancelKeyPress -= handler;
                ClosePhysical(m);
            }
        }

        // ------------------------------------------------------------------
        // 离线自检：G-Menu DDC/CI 帧与输入源编码
        // ------------------------------------------------------------------
        private static int RunSelfTest()
        {
            Console.WriteLine("ddc.exe 离线自检 —— G-Menu 反汇编还原的 DDC/CI 帧与输入源编码");
            Console.WriteLine("帧格式: 6E 51 <0x80+长度> <03=Set/01=Get> <命令...> <XOR 校验>");
            Console.WriteLine();

            int fails = 0;
            CheckSetFrame("DP1", SrcDp1, "6E 51 84 03 60 00 0F D7", ref fails);
            CheckSetFrame("DP2", SrcDp2, "6E 51 84 03 60 00 10 C8", ref fails);
            CheckSetFrame("HDMI1", SrcHdmi1, "6E 51 84 03 60 00 11 C9", ref fails);
            CheckSetFrame("HDMI2", SrcHdmi2, "6E 51 84 03 60 00 12 CA", ref fails);
            CheckGetFrame("查询", "6E 51 82 01 60 DC", ref fails);

            Console.WriteLine();
            if (fails == 0)
            {
                Console.WriteLine("[PASS] 5 项全部通过：输入源编码与 DDC/CI 帧与 G-Menu 反编译（StandardSource_E / 帧层）逐字节一致。");
                Console.WriteLine("       说明: 运行时经 dxva2 由显示驱动按同一 VCP 语义封装 DDC/CI 帧；本工具不直接拼发原始帧。");
                return 0;
            }

            Console.Error.WriteLine("[FAIL] {0} 项未通过，请检查输入源编码/校验和实现。", fails);
            return 4;
        }

        private static void CheckSetFrame(string name, uint code, string expected, ref int fails)
        {
            byte[] cmds = new byte[] { VcpInputSource, (byte)((code >> 8) & 0xFF), (byte)(code & 0xFF) };
            byte[] frame = BuildDdcFrame(false, cmds);
            string actual = Hex(frame);
            bool ok = actual == expected;
            Console.WriteLine("  {0} Set VCP 0x60 = 0x{1:X2}   {2}   {3}",
                name, code, actual, ok ? "[PASS]" : "[FAIL] (期望 " + expected + ")");
            if (!ok)
            {
                fails++;
            }
        }

        private static void CheckGetFrame(string name, string expected, ref int fails)
        {
            byte[] frame = BuildDdcFrame(true, new byte[] { VcpInputSource });
            string actual = Hex(frame);
            bool ok = actual == expected;
            Console.WriteLine("  {0} Get VCP 0x60   {1}   {2}",
                name, actual, ok ? "[PASS]" : "[FAIL] (期望 " + expected + ")");
            if (!ok)
            {
                fails++;
            }
        }

        private static byte[] BuildDdcFrame(bool isGet, byte[] cmds)
        {
            int n = cmds.Length;
            byte[] f = new byte[4 + n + 1];
            f[0] = 0x6E;
            f[1] = 0x51;
            f[2] = (byte)(0x80 + n + 1);
            f[3] = (byte)(isGet ? 0x01 : 0x03);
            Array.Copy(cmds, 0, f, 4, n);

            byte chk = 0;
            for (int i = 0; i < f.Length - 1; i++)
            {
                chk ^= f[i];
            }
            f[f.Length - 1] = chk;
            return f;
        }

        private static string Hex(byte[] data)
        {
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < data.Length; i++)
            {
                if (i > 0)
                {
                    sb.Append(' ');
                }
                sb.Append(data[i].ToString("X2"));
            }
            return sb.ToString();
        }
    }
}

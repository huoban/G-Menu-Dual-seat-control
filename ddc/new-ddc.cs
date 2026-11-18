// ============================================================================
// new-ddc.exe —— AOC AG275UXM2 输入源切换（正式版）
//
// 用法:
//   new-ddc.exe 1|2|3|4   直接发送切换信号（静默，无任何输出，执行完退出）
//       1 = HDMI1 (0x11)
//       2 = HDMI2 (0x12)
//       3 = DP1   (0x0F)
//       4 = DP2   (0x10)
//
//   new-ddc.exe           自动切换（静默，无任何输出）
//       当前 0x12 (HDMI2) → 切到 0x0F (DP1)
//       当前 0x0F (DP1)   → 切到 0x12 (HDMI2)
//       当前 0x11 (HDMI1) 或 0x10 (DP2) → 切到 0x0F (DP1)
//
//   new-ddc.exe debug1|debug2|debug3|debug4   带调试输出执行对应切换
//   new-ddc.exe debuglist                     列出显示器信息
//   new-ddc.exe help                          显示完整规则
//
// 协议: DDC/CI VCP 0x60（输入源），数值取自 G-Menu 反汇编 StandardSource_E:
//       HDMI1=0x11 HDMI2=0x12 DP1=0x0F DP2=0x10
// 传输: Windows 标准 MCCS / 物理监视器 API（dxva2.dll），沿视频线（DP1）的 DDC
//       信号通路下发；不依赖 USB 上行线，不加载任何厂商 DLL，无需 G-Menu。
//
// 退出码: 0=成功 1=参数错误 2=未找到显示器 3=DDC 写入失败
//
// 编译（Windows 自带 .NET Framework 4.x csc）:
//   C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe /nologo /target:exe
//       /platform:anycpu /optimize+ /codepage:65001 /out:new-ddc.exe new-ddc.cs
// ============================================================================

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Threading;

namespace AocDdcFinal
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

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr lprcClip, MonitorEnumProc lpfnEnum, IntPtr dwData);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFOEX lpmi);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern bool EnumDisplayDevices(string lpDevice, uint iDevNum, ref DISPLAY_DEVICE lpDisplayDevice, uint dwFlags);

        [DllImport("dxva2.dll", SetLastError = true)]
        public static extern bool GetNumberOfPhysicalMonitorsFromHMONITOR(IntPtr hMonitor, ref uint pdwNumberOfPhysicalMonitors);

        [DllImport("dxva2.dll", SetLastError = true)]
        public static extern bool GetPhysicalMonitorsFromHMONITOR(IntPtr hMonitor, uint dwPhysicalMonitorArraySize, [Out] PHYSICAL_MONITOR[] pPhysicalMonitorArray);

        [DllImport("dxva2.dll", SetLastError = true)]
        public static extern bool DestroyPhysicalMonitors(uint dwPhysicalMonitorArraySize, [In] PHYSICAL_MONITOR[] pPhysicalMonitorArray);

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
        private const byte VcpInputSource = 0x60;

        // 参数 1-4 → 输入源编码（G-Menu StandardSource_E）
        private static readonly uint[] Codes = new uint[] { 0x11, 0x12, 0x0F, 0x10 };
        private static readonly string[] Names = new string[] { "HDMI1", "HDMI2", "DP1", "DP2" };

        private static int Main(string[] args)
        {
            try
            {
                if (args.Length == 0)
                {
                    return AutoMode();
                }

                string cmd = args[0].Trim().ToLowerInvariant();
                switch (cmd)
                {
                    case "help":
                    case "-h":
                    case "--help":
                    case "/?":
                    case "?":
                        PrintHelp();
                        return 0;
                    case "debuglist":
                        return DebugList();
                    case "debug1":
                        return DebugSwitch(0);
                    case "debug2":
                        return DebugSwitch(1);
                    case "debug3":
                        return DebugSwitch(2);
                    case "debug4":
                        return DebugSwitch(3);
                    case "1":
                        return SilentSwitch(0);
                    case "2":
                        return SilentSwitch(1);
                    case "3":
                        return SilentSwitch(2);
                    case "4":
                        return SilentSwitch(3);
                    default:
                        Console.Error.WriteLine("参数无效: " + args[0]);
                        PrintHelp();
                        return 1;
                }
            }
            catch (Exception)
            {
                return 3;
            }
        }

        // ------------------------------------------------------------------
        // 静默模式：直接发送切换信号，无任何输出
        // ------------------------------------------------------------------
        private static int SilentSwitch(int index)
        {
            uint target = Codes[index];
            List<MonitorEntry> candidates = BuildCandidates();

            for (int i = 0; i < candidates.Count; i++)
            {
                MonitorEntry m = candidates[i];
                if (!OpenPhysical(m))
                {
                    continue;
                }

                try
                {
                    int e;
                    if (DdcWriteRetry(m.Physicals[0].hPhysicalMonitor, target, out e, 3))
                    {
                        return 0;
                    }
                }
                finally
                {
                    ClosePhysical(m);
                }
            }

            return 3;
        }

        // ------------------------------------------------------------------
        // 无参数：读取当前输入源后按规则自动切换（无任何输出）
        // ------------------------------------------------------------------
        private static int AutoMode()
        {
            List<MonitorEntry> candidates = BuildCandidates();

            for (int i = 0; i < candidates.Count; i++)
            {
                MonitorEntry m = candidates[i];
                if (!OpenPhysical(m))
                {
                    continue;
                }

                try
                {
                    IntPtr h = m.Physicals[0].hPhysicalMonitor;
                    uint cur;
                    uint max;
                    int e;

                    if (!DdcReadRetry(h, out cur, out max, out e, 2))
                    {
                        continue;
                    }

                    uint target;
                    if (cur == Codes[1])
                    {
                        target = Codes[2];          // 0x12 HDMI2 → 0x0F DP1
                    }
                    else if (cur == Codes[2])
                    {
                        target = Codes[1];          // 0x0F DP1 → 0x12 HDMI2
                    }
                    else
                    {
                        target = Codes[2];          // 0x11 HDMI1 / 0x10 DP2 / 其它 → 0x0F DP1
                    }

                    return DdcWriteRetry(h, target, out e, 3) ? 0 : 3;
                }
                finally
                {
                    ClosePhysical(m);
                }
            }

            // 读不到当前输入源时，按规则默认切到 DP1（静默）
            return SilentSwitch(2);
        }

        // ------------------------------------------------------------------
        // 调试模式：带详细输出执行切换
        // ------------------------------------------------------------------
        private static int DebugSwitch(int index)
        {
            uint target = Codes[index];
            string name = Names[index];

            List<MonitorEntry> all = EnumerateDisplays();
            if (all.Count == 0)
            {
                Console.Error.WriteLine("未检测到任何显示器。");
                return 2;
            }

            Console.WriteLine("检测到 {0} 个显示器，将按顺序尝试：", all.Count);
            List<MonitorEntry> candidates = OrderCandidates(all);
            for (int i = 0; i < candidates.Count; i++)
            {
                Console.WriteLine("  [{0}] {1}  {2}", i + 1, candidates[i].DeviceName,
                    candidates[i].Model.Length > 0 ? candidates[i].Model : "(未知型号)");
            }
            Console.WriteLine();

            bool anyWrite = false;
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

                try
                {
                    IntPtr h = m.Physicals[0].hPhysicalMonitor;

                    uint cur = 0;
                    uint max = 0;
                    int e;
                    bool readOk = DdcReadRetry(h, out cur, out max, out e, 3);

                    if (readOk)
                    {
                        Console.WriteLine("  当前输入源: 0x{0:X2} ({1})", cur, SourceName(cur));
                        if (cur == target)
                        {
                            Console.WriteLine("[OK] 当前已处于 {0}，无需切换。", name);
                            return 0;
                        }
                    }
                    else
                    {
                        Console.WriteLine("  当前输入源读取失败 (Win32 {0})，继续尝试。", WinErr(e));
                    }

                    if (!DdcWriteRetry(h, target, out e, 3))
                    {
                        Console.Error.WriteLine("  [失败] DDC 写入失败 (Win32 {0})", WinErr(e));
                        continue;
                    }
                    anyWrite = true;

                    Console.WriteLine("  [OK] 已发送切换命令，回读确认中 ...");

                    uint last = 0;
                    bool sawTarget;
                    bool anyRead;
                    int hits = PollForTarget(h, target, 20, out last, out sawTarget, out anyRead);

                    if (sawTarget)
                    {
                        Console.WriteLine("[OK] 显示器已确认切换到 {0}（回读 VCP 0x60 = 0x{1:X2}，第 {2} 次回读命中）。", name, target, hits);
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
            if (anyWrite)
            {
                Console.Error.WriteLine("[WARN] 已向 {0} 个候选显示器发送切换命令，但均未回读确认。", candidates.Count);
                if (lastSeen != 0xFFFFFFFF)
                {
                    Console.Error.WriteLine("       最后回读: 0x{0:X2} ({1})（{2}）。", lastSeen, SourceName(lastSeen), lastSeenName);
                }
                return 0;
            }

            Console.Error.WriteLine("[错误] 所有候选显示器的 DDC 写入均失败。");
            return 3;
        }

        // ------------------------------------------------------------------
        // debuglist：列出显示器
        // ------------------------------------------------------------------
        private static int DebugList()
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
            Console.WriteLine("提示: 调试切换用 debug1/debug2/debug3/debug4；详细说明用 help。");
            return 0;
        }

        // ------------------------------------------------------------------
        // 帮助
        // ------------------------------------------------------------------
        private static void PrintHelp()
        {
            Console.WriteLine("new-ddc.exe —— AOC AG275UXM2 输入源切换（正式版）");
            Console.WriteLine();
            Console.WriteLine("用法:");
            Console.WriteLine("  new-ddc.exe 1|2|3|4       直接发送切换信号（无输出，执行完退出）");
            Console.WriteLine("      1 = HDMI1 (0x11)");
            Console.WriteLine("      2 = HDMI2 (0x12)");
            Console.WriteLine("      3 = DP1   (0x0F)");
            Console.WriteLine("      4 = DP2   (0x10)");
            Console.WriteLine();
            Console.WriteLine("  new-ddc.exe               自动切换（无输出，执行完退出）");
            Console.WriteLine("      当前 0x12 (HDMI2) → 切到 0x0F (DP1)");
            Console.WriteLine("      当前 0x0F (DP1)   → 切到 0x12 (HDMI2)");
            Console.WriteLine("      当前 0x11 (HDMI1) 或 0x10 (DP2) → 切到 0x0F (DP1)");
            Console.WriteLine();
            Console.WriteLine("  new-ddc.exe debug1|debug2|debug3|debug4   带调试输出执行对应切换");
            Console.WriteLine("  new-ddc.exe debuglist                     列出显示器信息");
            Console.WriteLine("  new-ddc.exe help                          显示本说明");
            Console.WriteLine();
            Console.WriteLine("说明:");
            Console.WriteLine("  * 协议: DDC/CI VCP 0x60（Windows MCCS / dxva2 物理监视器 API），");
            Console.WriteLine("    经显示器视频线（DP1）的 DDC 信号通路下发；");
            Console.WriteLine("  * 不依赖显示器 USB 上行线，不加载厂商 DLL，无需运行 G-Menu；");
            Console.WriteLine("  * 静默模式（1-4 与无参数）不输出任何信息，仅以退出码反馈：");
            Console.WriteLine("      0=成功  1=参数错误  2=未找到显示器  3=DDC 写入失败");
            Console.WriteLine("  * 调试模式输出与 ddc.exe 一致（候选显示器、当前输入源、发送/回读结果）。");
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
                return string.CompareOrdinal(a.DeviceName, b.DeviceName);
            });

            return list;
        }

        private static List<MonitorEntry> BuildCandidates()
        {
            return OrderCandidates(EnumerateDisplays());
        }

        private static List<MonitorEntry> OrderCandidates(List<MonitorEntry> all)
        {
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
            return candidates;
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
            }
        }

        private static bool LooksLikeAoc(MonitorEntry m)
        {
            string s = (m.Model == null ? string.Empty : m.Model) + " " + (m.MonitorId == null ? string.Empty : m.MonitorId);
            s = s.ToUpperInvariant();
            return s.Contains("AOC") || s.Contains("AGON") || s.Contains("AG27");
        }

        private static bool OpenPhysical(MonitorEntry entry)
        {
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
        // DDC/CI 读写
        // ------------------------------------------------------------------
        private static bool DdcReadRetry(IntPtr hPhysical, out uint current, out uint maximum, out int win32Error, int attempts)
        {
            current = 0;
            maximum = 0;
            win32Error = 0;
            for (int i = 0; i < attempts; i++)
            {
                if (NativeMethods.GetVCPFeatureAndVCPFeatureReply(hPhysical, VcpInputSource, IntPtr.Zero, ref current, ref maximum))
                {
                    return true;
                }
                win32Error = Marshal.GetLastWin32Error();
                if (i + 1 < attempts)
                {
                    Thread.Sleep(200);
                }
            }
            return false;
        }

        private static bool DdcWriteRetry(IntPtr hPhysical, uint value, out int win32Error, int attempts)
        {
            win32Error = 0;
            for (int i = 0; i < attempts; i++)
            {
                if (NativeMethods.SetVCPFeature(hPhysical, VcpInputSource, value))
                {
                    return true;
                }
                win32Error = Marshal.GetLastWin32Error();
                if (i + 1 < attempts)
                {
                    Thread.Sleep(250);
                }
            }
            return false;
        }

        private static int PollForTarget(IntPtr hPhysical, uint target, int attempts, out uint lastValue, out bool sawTarget, out bool anyRead)
        {
            lastValue = 0;
            sawTarget = false;
            anyRead = false;
            for (int i = 0; i < attempts; i++)
            {
                uint cur = 0;
                uint max = 0;
                if (NativeMethods.GetVCPFeatureAndVCPFeatureReply(hPhysical, VcpInputSource, IntPtr.Zero, ref cur, ref max))
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
                case 0x01:
                    return "VGA(D-SUB)";
                case 0x03:
                    return "DVI1";
                case 0x0F:
                    return "DP1";
                case 0x10:
                    return "DP2";
                case 0x11:
                    return "HDMI1";
                case 0x12:
                    return "HDMI2";
                case 0x13:
                    return "HDMI3";
                case 0x15:
                    return "USB-C1";
                case 0x16:
                    return "USB-C2";
                default:
                    return "未知输入源";
            }
        }
    }
}

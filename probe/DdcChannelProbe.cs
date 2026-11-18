// AOC AG275UXM2 双席位 DDC 通道诊断探针（只读，不做任何切换）
//
// 目的：在同一台主机的两个 ASTER 席位分别运行，判定「G-Menu 能切、自研工具不能切」的差异来源。
//   通道 A：G-Menu 自带的原生库 DDCHelperLib.dll（G-Menu 实际使用的多后端 DDC 库：
//           dxva2 / NVAPI / AMD ADL / Intel 直连 / MtxApi / \\.\MDDCDRV）
//   通道 B：Windows MCCS/dxva2 物理监视器 API（new-ddc 使用的通道）
//
// 编译（必须 x86，DDCHelperLib.dll 为 32 位）：
//   C:\Windows\Microsoft.NET\Framework\v4.0.30319\csc.exe /nologo /platform:x86 /codepage:65001 ^
//       /out:DdcChannelProbe.exe DdcChannelProbe.cs
//
// 用法：
//   DdcChannelProbe.exe             自动在同目录 / G-Menu 安装目录查找 DDCHelperLib.dll
//   DdcChannelProbe.exe --lib "D:\path\DDCHelperLib.dll"
//   DdcChannelProbe.exe --max-index 15

using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace DdcChannelProbe
{
    // ===== DDCHelperLib 结构体（与 Zeasn.DDC.Lib.dll 反编译字段逐一对齐）=====
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
    internal struct EnumDisplayId
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 520)]
        public string Name;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 520)]
        public int[] Ids;
        public int DefaultIndex;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 520)]
        public string Extra;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct DdcValue
    {
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 520)]
        public byte[] Cmd;
        public byte Flag1;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 520)]
        public byte[] Resp;
        public byte Flag2;
        public int DisplayIndex;
        public byte VcpCode;
        public int Result;
        public int Mode;
        public int MaxValue;
    }

    // ===== dxva2 结构体 =====
    [StructLayout(LayoutKind.Sequential)]
    internal struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct MONITORINFOEX
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string szDevice;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct DISPLAY_DEVICE
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
    internal struct PHYSICAL_MONITOR
    {
        public IntPtr hPhysicalMonitor;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szPhysicalMonitorDescription;
    }

    internal static class Dxva2
    {
        public delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdc, ref RECT rect, IntPtr data);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc proc, IntPtr data);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFOEX mi);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern bool EnumDisplayDevices(string device, uint index, ref DISPLAY_DEVICE dd, uint flags);

        [DllImport("dxva2.dll", SetLastError = true)]
        public static extern bool GetNumberOfPhysicalMonitorsFromHMONITOR(IntPtr hMonitor, ref uint count);

        [DllImport("dxva2.dll", SetLastError = true)]
        public static extern bool GetPhysicalMonitorsFromHMONITOR(IntPtr hMonitor, uint size, [Out] PHYSICAL_MONITOR[] arr);

        [DllImport("dxva2.dll", SetLastError = true)]
        public static extern bool DestroyPhysicalMonitors(uint size, [In] PHYSICAL_MONITOR[] arr);

        [DllImport("dxva2.dll", SetLastError = true)]
        public static extern bool GetVCPFeatureAndVCPFeatureReply(IntPtr h, byte code, IntPtr type, ref uint current, ref uint max);

        [DllImport("dxva2.dll", SetLastError = true)]
        public static extern bool SetVCPFeature(IntPtr h, byte code, uint value);
    }

    internal sealed class DdcHelper
    {
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        internal delegate int InitFn();
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        internal delegate int EnumFn(ref EnumDisplayId p);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        internal delegate int GetFn(ref DdcValue p);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        internal delegate int SetFn(ref DdcValue p);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        internal delegate IntPtr LogFn();

        private IntPtr _h;
        public string LibPath { get; private set; }
        public InitFn Init;
        public EnumFn EnumDisplay;
        public GetFn GetValue;
        public SetFn SetValue;
        public LogFn Log;

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr LoadLibraryExW(string path, IntPtr file, uint flags);

        private const uint LOAD_WITH_ALTERED_SEARCH_PATH = 0x00000008;

        [DllImport("kernel32.dll", CharSet = CharSet.Ansi, SetLastError = true)]
        private static extern IntPtr GetProcAddress(IntPtr h, string name);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool FreeLibrary(IntPtr h);

        public static DdcHelper TryLoad(string explicitPath)
        {
            foreach (string path in CandidatePaths(explicitPath))
            {
                if (string.IsNullOrEmpty(path) || !File.Exists(path)) continue;
                IntPtr h;
                try { h = LoadLibraryExW(path, IntPtr.Zero, LOAD_WITH_ALTERED_SEARCH_PATH); }
                catch { h = IntPtr.Zero; }
                if (h == IntPtr.Zero)
                {
                    int err = Marshal.GetLastWin32Error();
                    Console.WriteLine("  [加载失败] {0}  err={1} {2}", path, err, new System.ComponentModel.Win32Exception(err).Message);
                    continue;
                }
                var d = new DdcHelper { _h = h, LibPath = path };
                d.Init = Bind<InitFn>(h, "DDCCIHelperIni");
                d.EnumDisplay = Bind<EnumFn>(h, "EnumDisplayIDIni");
                d.GetValue = Bind<GetFn>(h, "getStandardDDCCIValue_C");
                d.SetValue = Bind<SetFn>(h, "setStandardDDCCIValue_C");
                d.Log = Bind<LogFn>(h, "logGetDDCDrv");
                if (d.Init != null && d.EnumDisplay != null && d.GetValue != null) return d;
                FreeLibrary(h);
            }
            return null;
        }

        private static T Bind<T>(IntPtr h, string name) where T : class
        {
            // 原生库可能以未修饰名 / A-W 变体 / stdcall 修饰名导出，逐一尝试
            string[] tries = { name, name + "A", name + "W", "_" + name + "@0", "_" + name + "@4", "_" + name + "@8" };
            foreach (string n in tries)
            {
                IntPtr p = GetProcAddress(h, n);
                if (p == IntPtr.Zero) continue;
                return (T)(object)Marshal.GetDelegateForFunctionPointer(p, typeof(T));
            }
            Console.WriteLine("  [绑定失败] 导出表中未找到 {0}", name);
            return null;
        }

        private static IEnumerable<string> CandidatePaths(string explicitPath)
        {
            if (!string.IsNullOrEmpty(explicitPath)) yield return explicitPath;
            yield return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "DDCHelperLib.dll");

            var roots = new List<string>();
            string pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            string pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            foreach (string r in new[] { pf, pf86, local, @"C:\Program Files", @"C:\Program Files (x86)" })
            {
                if (string.IsNullOrEmpty(r)) continue;
                roots.Add(Path.Combine(r, "G-Menu", "resources", "bin", "DDCHelperLib.dll"));
                roots.Add(Path.Combine(r, "G-Menu", "bin", "DDCHelperLib.dll"));
                roots.Add(Path.Combine(r, "AOC", "G-Menu", "resources", "bin", "DDCHelperLib.dll"));
                roots.Add(Path.Combine(r, "Programs", "G-Menu", "resources", "bin", "DDCHelperLib.dll"));
            }
            foreach (string r in roots) yield return r;

            // 注册表卸载项定位安装目录
            foreach (string regRoot in new[] { @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall", @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall" })
            {
                string found = SearchUninstall(Registry.LocalMachine, regRoot);
                if (found != null) yield return found;
                found = SearchUninstall(Registry.CurrentUser, regRoot);
                if (found != null) yield return found;
            }
        }

        private static string SearchUninstall(RegistryKey root, string sub)
        {
            try
            {
                using (RegistryKey k = root.OpenSubKey(sub))
                {
                    if (k == null) return null;
                    foreach (string name in k.GetSubKeyNames())
                    {
                        using (RegistryKey item = k.OpenSubKey(name))
                        {
                            if (item == null) continue;
                            string dn = item.GetValue("DisplayName") as string;
                            string loc = (item.GetValue("InstallLocation") as string) ?? (item.GetValue("DisplayIcon") as string);
                            if (dn == null || loc == null) continue;
                            if (dn.IndexOf("G-Menu", StringComparison.OrdinalIgnoreCase) < 0) continue;
                            string dir = loc.Trim('"');
                            if (dir.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) dir = Path.GetDirectoryName(dir);
                            if (string.IsNullOrEmpty(dir)) continue;
                            string p1 = Path.Combine(dir, "resources", "bin", "DDCHelperLib.dll");
                            if (File.Exists(p1)) return p1;
                            string p2 = Path.Combine(dir, "bin", "DDCHelperLib.dll");
                            if (File.Exists(p2)) return p2;
                        }
                    }
                }
            }
            catch { }
            return null;
        }
    }

    internal static class Program
    {
        private const byte VCP_INPUT = 0x60;
        private static DdcHelper _ddc;
        private static int _maxIndex = 15;

        private static int Main(string[] args)
        {
            Console.OutputEncoding = Encoding.UTF8;
            string lib = null;
            for (int i = 0; i < args.Length; i++)
            {
                if ((args[i] == "--lib" || args[i] == "/lib") && i + 1 < args.Length) lib = args[++i];
                else if ((args[i] == "--max-index" || args[i] == "/max-index") && i + 1 < args.Length) int.TryParse(args[++i], out _maxIndex);
            }

            Console.WriteLine("================ AOC 双席位 DDC 通道诊断 ================");
            Console.WriteLine("时间          : {0:yyyy-MM-dd HH:mm:ss}", DateTime.Now);
            Console.WriteLine("进程位数      : {0} bit", IntPtr.Size * 8);
            Console.WriteLine("会话 ID       : {0}", System.Diagnostics.Process.GetCurrentProcess().SessionId);
            Console.WriteLine("用户          : {0}", Environment.UserName);
            Console.WriteLine("机器          : {0}", Environment.MachineName);
            Console.WriteLine();

            PrintGpus();

            Console.WriteLine("---------------- 通道 A：DDCHelperLib（G-Menu 同款）----------------");
            _ddc = DdcHelper.TryLoad(lib);
            if (_ddc == null)
            {
                Console.WriteLine("[不可用] 未找到或无法加载 DDCHelperLib.dll");
                Console.WriteLine("         请用 --lib 指定 G-Menu 安装目录下的 DDCHelperLib.dll");
            }
            else
            {
                Console.WriteLine("[已加载] {0}", _ddc.LibPath);
                ProbeDdcHelper();
            }

            Console.WriteLine();
            Console.WriteLine("---------------- 通道 B：dxva2 / MCCS（new-ddc 同款）----------------");
            ProbeDxva2();

            Console.WriteLine();
            Console.WriteLine("---------------- GDI 显示设备（EnumDisplayDevices）----------------");
            PrintGdiDevices();

            Console.WriteLine();
            Console.WriteLine("================ 诊断结束（本工具只读，未执行任何切换）================");
            return 0;
        }

        private static void PrintGpus()
        {
            Console.WriteLine("---------------- 显示适配器（WMI）----------------");
            try
            {
                var t = Type.GetTypeFromProgID("WbemScripting.SWbemLocator");
                if (t == null) { Console.WriteLine("(WMI 不可用)"); return; }
                dynamic loc = Activator.CreateInstance(t);
                dynamic svc = loc.ConnectServer(".", @"root\cimv2");
                dynamic list = svc.ExecQuery("SELECT Name,DriverVersion,Status FROM Win32_VideoController");
                foreach (dynamic v in list)
                    Console.WriteLine("  {0} | 驱动 {1} | {2}", v.Name, v.DriverVersion, v.Status);
            }
            catch (Exception ex) { Console.WriteLine("  (枚举失败: {0})", ex.Message); }
            Console.WriteLine();
        }

        private static void ProbeDdcHelper()
        {
            int rc = SafeCall(() => _ddc.Init(), "DDCCIHelperIni");
            Console.WriteLine("DDCCIHelperIni()          -> {0} (0x{0:X8})", rc);

            var id = new EnumDisplayId { Ids = new int[520] };
            int rcEnum = SafeCall(() => _ddc.EnumDisplay(ref id), "EnumDisplayIDIni");
            Console.WriteLine("EnumDisplayIDIni()        -> {0} (0x{0:X8})", rcEnum);
            Console.WriteLine("  Name         = \"{0}\"", id.Name);
            Console.WriteLine("  DefaultIndex = {0}", id.DefaultIndex);
            Console.WriteLine("  Extra        = \"{0}\"", id.Extra);
            Console.Write("  Ids[0..15]   = ");
            var sb = new StringBuilder();
            for (int i = 0; i < 16 && i < id.Ids.Length; i++) sb.Append(id.Ids[i]).Append(' ');
            Console.WriteLine(sb.ToString().Trim());

            Console.WriteLine();
            Console.WriteLine("逐索引读取 VCP 0x60（当前输入源），找出可用索引：");
            // 语义（与 Zeasn.DDC.Lib.vpf1gYOf0 对齐）：rc==0 成功；Result = 读到的当前值；MaxValue 字段 = VCP 最大值
            for (int idx = 0; idx <= _maxIndex; idx++)
            {
                var v = NewRequest(idx, VCP_INPUT);
                int rcGet = SafeCall(() => _ddc.GetValue(ref v), "getStandardDDCCIValue_C");
                bool ok = rcGet == 0;
                Console.WriteLine("  index {0,2}: rc=0x{1:X8} 当前值=0x{2:X4}{3} 最大值=0x{4:X4} Resp[0..7]={5}  {6}",
                    idx, rcGet, v.Result,
                    ok ? " (" + SourceName((byte)v.Result) + ")" : "",
                    v.MaxValue, Hex(v.Resp, 8), ok ? "<== 可用" : "");
                if (ok) break;
            }

            string log = ReadLog();
            if (!string.IsNullOrEmpty(log))
            {
                Console.WriteLine();
                Console.WriteLine("DDCHelperLib 内部日志：");
                foreach (string line in log.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                    if (line.Trim().Length > 0) Console.WriteLine("  | " + line.Trim());
            }
        }

        private static DdcValue NewRequest(int index, byte vcp)
        {
            var v = new DdcValue
            {
                Cmd = new byte[520],
                Resp = new byte[520],
                DisplayIndex = index,
                VcpCode = vcp,
                Mode = 1,
                Flag1 = 2
            };
            v.Cmd[0] = 1;
            v.Cmd[1] = vcp;
            return v;
        }

        private static string ReadLog()
        {
            try
            {
                if (_ddc.Log == null) return null;
                IntPtr p = _ddc.Log();
                if (p == IntPtr.Zero) return null;
                return Marshal.PtrToStringAnsi(p);
            }
            catch { return null; }
        }

        private static void ProbeDxva2()
        {
            var monitors = new List<IntPtr>();
            Dxva2.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, delegate (IntPtr h, IntPtr hdc, ref RECT r, IntPtr d)
            {
                monitors.Add(h);
                return true;
            }, IntPtr.Zero);
            Console.WriteLine("EnumDisplayMonitors 数量: {0}", monitors.Count);

            foreach (IntPtr hMon in monitors)
            {
                var mi = new MONITORINFOEX { cbSize = Marshal.SizeOf(typeof(MONITORINFOEX)) };
                Dxva2.GetMonitorInfo(hMon, ref mi);
                uint count = 0;
                if (!Dxva2.GetNumberOfPhysicalMonitorsFromHMONITOR(hMon, ref count) || count == 0)
                {
                    Console.WriteLine("  {0}: 无物理监视器 (err={1})", mi.szDevice, Marshal.GetLastWin32Error());
                    continue;
                }
                var arr = new PHYSICAL_MONITOR[count];
                if (!Dxva2.GetPhysicalMonitorsFromHMONITOR(hMon, count, arr))
                {
                    Console.WriteLine("  {0}: GetPhysicalMonitors 失败 (err={1})", mi.szDevice, Marshal.GetLastWin32Error());
                    continue;
                }
                try
                {
                    foreach (var pm in arr)
                    {
                        uint cur = 0, max = 0;
                        bool okRead = Dxva2.GetVCPFeatureAndVCPFeatureReply(pm.hPhysicalMonitor, VCP_INPUT, IntPtr.Zero, ref cur, ref max);
                        int err = Marshal.GetLastWin32Error();
                        Console.WriteLine("  {0} | {1}", mi.szDevice, pm.szPhysicalMonitorDescription);
                        if (okRead)
                            Console.WriteLine("      VCP 0x60 读取成功: 0x{0:X2} ({1})  max=0x{2:X}", cur, SourceName((byte)cur), max);
                        else
                            Console.WriteLine("      VCP 0x60 读取失败: Win32 {0} (0x{0:X8}) {1}", err, new System.ComponentModel.Win32Exception(err).Message);
                    }
                }
                finally
                {
                    Dxva2.DestroyPhysicalMonitors(count, arr);
                }
            }
        }

        private static void PrintGdiDevices()
        {
            uint i = 0;
            while (true)
            {
                var dd = new DISPLAY_DEVICE { cb = Marshal.SizeOf(typeof(DISPLAY_DEVICE)) };
                if (!Dxva2.EnumDisplayDevices(null, i, ref dd, 0)) break;
                i++;
                Console.WriteLine("  [{0}] {1} | {2} | flags=0x{3:X} | ID={4}", i, dd.DeviceName, dd.DeviceString, dd.StateFlags, dd.DeviceID);
                uint j = 0;
                while (true)
                {
                    var md = new DISPLAY_DEVICE { cb = Marshal.SizeOf(typeof(DISPLAY_DEVICE)) };
                    if (!Dxva2.EnumDisplayDevices(dd.DeviceName, j, ref md, 0)) break;
                    j++;
                    Console.WriteLine("        monitor[{0}]: {1} | ID={2}", j, md.DeviceString, md.DeviceID);
                }
            }
        }

        private static int SafeCall(Func<int> f, string name)
        {
            try { return f(); }
            catch (Exception ex)
            {
                Console.WriteLine("  [{0} 异常] {1}", name, ex.Message);
                return -1;
            }
        }

        private static string Hex(byte[] b, int n)
        {
            if (b == null) return "";
            var sb = new StringBuilder();
            for (int i = 0; i < n && i < b.Length; i++) sb.Append(b[i].ToString("X2")).Append(' ');
            return sb.ToString().Trim();
        }

        private static string SourceName(byte v)
        {
            switch (v)
            {
                case 0x0F: return "DP1";
                case 0x10: return "DP2";
                case 0x11: return "HDMI1";
                case 0x12: return "HDMI2";
                case 0x15: return "USB-C1";
                case 0x16: return "USB-C2";
                default: return "其它";
            }
        }
    }
}

// aoc-switch.exe —— AOC AG275UXM2 输入源切换（DDCHelper + NVAPI + dxva2）
//
// 背景（2026-10-19 重新梳理）：
//   G-Menu 的 DDC 并不只走 dxva2。它的托管层 Zeasn.DDC.Lib.dll 通过 P/Invoke 调用
//   原生库 DDCHelperLib.dll，而该库内含多后端实现（从导入表与字符串确认）：
//     * dxva2.dll : GetPhysicalMonitorsFromHMONITOR / SetVCPFeature / GetVCPFeatureAndVCPFeatureReply
//     * nvapi.dll / nvapi64.dll : nvapi_QueryInterface（NVIDIA I2C 通路，系统级，跨会话可用）
//     * atiadlxx/atiadlxy/atiddc : ADL_Display_WriteAndReadI2C（AMD 通路）
//     * Intel 直连 : OpenIntelCard / i_InitSDVOBus / sdvo_ddc_i2c_put_byte
//     * MtxApi.dll 与可选的 \\.\MDDCDRV 专用驱动
//   因此「G-Menu 能切、new-ddc.exe 不能切」的差异，本质是通道差异，不是协议差异。
//
// 通道策略（按顺序尝试）：
//   A: DDCHelperLib.dll —— 与 G-Menu 完全相同的库与后端选择逻辑（只从运行目录加载）
//   B: NVAPI —— 直接对主机上的 NVIDIA GPU 做 I2C/DDC，不受当前会话显示拓扑限制
//   C: dxva2 / MCCS —— 与 new-ddc.exe 相同，零依赖
//
// 关键实现要点：
//   * DDCHelperLib 必须先调 DDCCIHelperIni → EnumDisplayIDIni 完成显示器枚举，再读/写 VCP；
//   * getStandardDDCCIValue_C 返回 0 表示成功，DdcValue.Result = 当前值，DdcValue.Value = 最大值。
//
// 编译（必须 x86，DDCHelperLib.dll 为 32 位原生库）：
//   C:\Windows\Microsoft.NET\Framework\v4.0.30319\csc.exe /nologo /platform:x86 /codepage:65001 ^
//       /win32icon:icon.ico /out:aoc-switch.exe aoc-switch.cs
//
// 用法：
//   aoc-switch.exe 1|2|3|4        直接切换（静默）：1=HDMI1(0x11) 2=HDMI2(0x12) 3=DP1(0x0F) 4=DP2(0x10)
//   aoc-switch.exe                自动切换（静默）：0x12→0x0F；0x0F→0x12；其它→0x0F
//   aoc-switch.exe diag           完整诊断（只读）
//   aoc-switch.exe list           列出通道与显示器
//   aoc-switch.exe debug1|..|4    带输出执行切换
//   aoc-switch.exe help
//
// DDCHelperLib.dll 必须与本程序同目录；缺失时程序会提示并等待回车退出。
// 可选参数：--lib "<路径>\DDCHelperLib.dll"（调试用，指定后不再查运行目录）
//
// 退出码：0=成功 1=参数错误 2=未找到 DDCHelperLib.dll / 显示器 3=切换失败

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace AocSwitch
{
    // ===================== DDCHelperLib 结构体（与 Zeasn.DDC.Lib.dll 反编译逐字段对齐）=====================
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
        public int Result;   // Get: 读到的当前值；Set: 要写入的值
        public int Mode;
        public int Value;    // Get: VCP 最大值
    }

    internal sealed class DdcHelper
    {
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] internal delegate int InitFn();
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] internal delegate int EnumFn(ref EnumDisplayId p);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] internal delegate int GetFn(ref DdcValue p);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] internal delegate int SetFn(ref DdcValue p);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] internal delegate IntPtr LogFn();

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr LoadLibraryExW(string path, IntPtr file, uint flags);
        [DllImport("kernel32.dll", CharSet = CharSet.Ansi, SetLastError = true)]
        private static extern IntPtr GetProcAddress(IntPtr h, string name);

        public string LibPath;
        public InitFn Init;
        public EnumFn EnumDisplays;
        public GetFn Get;
        public SetFn Set;
        public LogFn Log;

        public static string DefaultPath
        {
            get { return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "DDCHelperLib.dll"); }
        }

        // 只从「软件运行目录」加载（或用 --lib 显式指定），不扫描磁盘、不写任何缓存文件
        public static DdcHelper Load(string explicitPath)
        {
            var candidates = new List<string>();
            if (!string.IsNullOrEmpty(explicitPath)) candidates.Add(explicitPath);
            candidates.Add(DefaultPath);
            foreach (string p in candidates)
            {
                DdcHelper d = TryOpen(p);
                if (d != null) return d;
            }
            return null;
        }

        private static DdcHelper TryOpen(string path)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
            IntPtr h = LoadLibraryExW(path, IntPtr.Zero, 0x8);
            if (h == IntPtr.Zero) return null;
            var d = new DdcHelper { LibPath = path };
            d.Init = Bind<InitFn>(h, "DDCCIHelperIni");
            d.EnumDisplays = Bind<EnumFn>(h, "EnumDisplayIDIni");
            d.Get = Bind<GetFn>(h, "getStandardDDCCIValue_C");
            d.Set = Bind<SetFn>(h, "setStandardDDCCIValue_C");
            d.Log = Bind<LogFn>(h, "logGetDDCDrv");
            if (d.Init == null || d.Get == null || d.Set == null) return null;
            return d;
        }

        private static T Bind<T>(IntPtr h, string name) where T : class
        {
            foreach (string n in new[] { name, name + "A", name + "W", "_" + name + "@0", "_" + name + "@4", "_" + name + "@8" })
            {
                IntPtr p = GetProcAddress(h, n);
                if (p != IntPtr.Zero) return (T)(object)Marshal.GetDelegateForFunctionPointer(p, typeof(T));
            }
            return null;
        }

        // --- 请求构造（与 Zeasn.DDC.Lib 反编译字段逐一对应）---
        public DdcValue NewGet(int index, byte vcp)
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

        public DdcValue NewSet(int index, byte vcp, ushort value)
        {
            var v = new DdcValue
            {
                Cmd = new byte[520],
                Resp = new byte[520],
                DisplayIndex = index,
                VcpCode = vcp,
                Mode = 1,
                Flag1 = 4,
                Result = value
            };
            v.Cmd[0] = 3;
            v.Cmd[1] = vcp;
            v.Cmd[2] = (byte)((value >> 8) & 0xFF);
            v.Cmd[3] = (byte)(value & 0xFF);
            return v;
        }

        public string ReadLog()
        {
            try
            {
                if (Log == null) return null;
                IntPtr p = Log();
                return p == IntPtr.Zero ? null : Marshal.PtrToStringAnsi(p);
            }
            catch { return null; }
        }
    }

    // ===================== NVAPI（系统级，跨会话）=====================
    [StructLayout(LayoutKind.Sequential)]
    internal struct NvI2cInfo
    {
        public uint version;
        public uint displayMask;
        public byte bIsDDCPort;
        public byte i2cDevAddress;
        public short pad;
        public IntPtr pbI2cRegAddress;
        public uint regAddrSize;
        public IntPtr pbData;
        public uint cbSize;
        public uint i2cSpeed;
    }

    internal sealed class NvApi
    {
        private const uint ID_INIT = 0x0150E828;
        private const uint ID_ENUM_GPUS = 0xE5AC921F;
        private const uint ID_GET_DISPLAY_IDS = 0x0078DBA2;
        private const uint ID_I2C_READ = 0x2FDE12C5;
        private const uint ID_I2C_WRITE = 0xE812EB07;

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr QueryInterfaceFn(uint id);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int InitFn();
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int EnumGpusFn([Out] IntPtr[] handles, ref uint count);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int GetDisplayIdsFn(IntPtr gpu, IntPtr info, uint flags);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int I2cFn(IntPtr gpu, IntPtr info);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr LoadLibraryW(string path);
        [DllImport("kernel32.dll", CharSet = CharSet.Ansi, SetLastError = true)]
        private static extern IntPtr GetProcAddress(IntPtr h, string name);

        private InitFn _init;
        private EnumGpusFn _enumGpus;
        private GetDisplayIdsFn _getIds;
        private I2cFn _i2cRead, _i2cWrite;
        private readonly byte[] _rx = new byte[64];

        public bool Ready { get; private set; }
        public List<IntPtr> Gpus = new List<IntPtr>();
        public List<uint> Masks = new List<uint>();
        public int DisplayIdsStatus;
        public string Detail = "未找到可用的 nvapi.dll（本机未安装 NVIDIA 驱动？）";
        public string Items = "";

        public static NvApi TryCreate()
        {
            var api = new NvApi();
            foreach (string dll in new[] { "nvapi.dll", "nvapi64.dll" })
            {
                IntPtr h = LoadLibraryW(dll);
                if (h == IntPtr.Zero) continue;
                IntPtr qi = GetProcAddress(h, "nvapi_QueryInterface");
                if (qi == IntPtr.Zero) continue;
                var query = (QueryInterfaceFn)Marshal.GetDelegateForFunctionPointer(qi, typeof(QueryInterfaceFn));
                api._init = Fn<InitFn>(query, ID_INIT);
                api._enumGpus = Fn<EnumGpusFn>(query, ID_ENUM_GPUS);
                api._getIds = Fn<GetDisplayIdsFn>(query, ID_GET_DISPLAY_IDS);
                api._i2cRead = Fn<I2cFn>(query, ID_I2C_READ);
                api._i2cWrite = Fn<I2cFn>(query, ID_I2C_WRITE);
                if (api._init == null || api._enumGpus == null || api._i2cRead == null || api._i2cWrite == null) continue;

                int rc = api._init();
                if (rc != 0) { api.Detail = string.Format("{0}: NvAPI_Initialize=0x{1:X8}", dll, rc); continue; }

                var handles = new IntPtr[64];
                uint count = 0;
                rc = api._enumGpus(handles, ref count);
                if (rc != 0 || count == 0)
                {
                    api.Detail = string.Format("{0}: EnumPhysicalGPUs=0x{1:X8} count={2}", dll, rc, count);
                    continue;
                }
                for (int i = 0; i < count && i < handles.Length; i++) api.Gpus.Add(handles[i]);
                api.CollectMasks();
                api.Items = dll;
                api.Ready = true;
                api.Detail = string.Format("{0}: GPU={1} mask={2}", dll, count, api.MaskText());
                return api;
            }
            return api;
        }

        public string MaskText()
        {
            var sb = new StringBuilder();
            foreach (uint m in Masks) sb.Append("0x").Append(m.ToString("X")).Append(' ');
            return sb.ToString().Trim();
        }

        private static T Fn<T>(QueryInterfaceFn query, uint id) where T : class
        {
            IntPtr p = query(id);
            if (p == IntPtr.Zero) return null;
            return (T)(object)Marshal.GetDelegateForFunctionPointer(p, typeof(T));
        }

        private void CollectMasks()
        {
            Masks.Clear();
            if (_getIds == null) { Masks.Add(0x1); return; }
            foreach (IntPtr gpu in Gpus)
            {
                const int size = 4 + 4 + 32 * 8;
                IntPtr buf = Marshal.AllocHGlobal(size);
                try
                {
                    for (int i = 0; i < size; i++) Marshal.WriteByte(buf, i, 0);
                    Marshal.WriteInt32(buf, 0, (int)((uint)size | (uint)(2 << 16)));
                    int rc = _getIds(gpu, buf, 0);
                    DisplayIdsStatus = rc;
                    if (rc != 0) continue;
                    uint n = (uint)Marshal.ReadInt32(buf, 4);
                    if (n > 32) n = 32;
                    for (uint i = 0; i < n; i++)
                    {
                        int id = Marshal.ReadInt32(buf, 8 + (int)i * 8);
                        if (id >= 0 && id < 32)
                        {
                            uint mask = 1u << id;
                            if (!Masks.Contains(mask)) Masks.Add(mask);
                        }
                    }
                }
                finally { Marshal.FreeHGlobal(buf); }
            }
            if (Masks.Count == 0) Masks.Add(0x1);
        }

        private int Transfer(IntPtr gpu, uint mask, byte[] tx, int rxLen)
        {
            int size = Marshal.SizeOf(typeof(NvI2cInfo));
            IntPtr pData = Marshal.AllocHGlobal(4096);
            IntPtr pInfo = Marshal.AllocHGlobal(size);
            try
            {
                for (int i = 0; i < 4096; i++) Marshal.WriteByte(pData, i, 0);
                if (tx != null) Marshal.Copy(tx, 0, pData, tx.Length);
                var info = new NvI2cInfo
                {
                    version = (uint)size | (1u << 16),
                    displayMask = mask,
                    bIsDDCPort = 1,
                    i2cDevAddress = 0x6E,
                    pbI2cRegAddress = IntPtr.Zero,
                    regAddrSize = 0,
                    pbData = pData,
                    cbSize = (uint)(tx != null ? tx.Length : rxLen),
                    i2cSpeed = 0xFFFF
                };
                Marshal.StructureToPtr(info, pInfo, false);
                int rc = tx != null ? _i2cWrite(gpu, pInfo) : _i2cRead(gpu, pInfo);
                if (rc == 0 && tx == null) Marshal.Copy(pData, _rx, 0, Math.Min(rxLen, _rx.Length));
                return rc;
            }
            finally
            {
                Marshal.FreeHGlobal(pData);
                Marshal.FreeHGlobal(pInfo);
            }
        }

        public static byte[] FrameSet(ushort value)
        {
            var f = new byte[] { 0x51, 0x84, 0x03, 0x60, (byte)(value >> 8), (byte)(value & 0xFF), 0x00 };
            byte chk = 0x6E;
            for (int i = 0; i < f.Length - 1; i++) chk ^= f[i];
            f[f.Length - 1] = chk;
            return f;
        }

        public static byte[] FrameGet()
        {
            var f = new byte[] { 0x51, 0x82, 0x01, 0x60, 0x00 };
            byte chk = 0x6E;
            for (int i = 0; i < f.Length - 1; i++) chk ^= f[i];
            f[f.Length - 1] = chk;
            return f;
        }

        public bool TryRead(IntPtr gpu, uint mask, out ushort value, out int rc)
        {
            value = 0;
            rc = Transfer(gpu, mask, FrameGet(), 0);
            if (rc != 0) return false;
            Thread.Sleep(60);
            rc = Transfer(gpu, mask, null, 12);
            if (rc != 0) return false;
            for (int i = 0; i + 8 < _rx.Length; i++)
            {
                if (_rx[i] == 0x6E && (_rx[i + 1] & 0x80) != 0)
                {
                    value = (ushort)((_rx[i + 7] << 8) | _rx[i + 8]);
                    return true;
                }
            }
            return false;
        }

        public int Write(IntPtr gpu, uint mask, ushort value)
        {
            return Transfer(gpu, mask, FrameSet(value), 0);
        }
    }

    // ===================== dxva2 / MCCS =====================
    [StructLayout(LayoutKind.Sequential)] internal struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct MONITORINFOEX
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szDevice;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct PHYSICAL_MONITOR
    {
        public IntPtr hPhysicalMonitor;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szPhysicalMonitorDescription;
    }

    internal sealed class Dxva2
    {
        public delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdc, ref RECT rect, IntPtr data);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc proc, IntPtr data);
        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFOEX mi);
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

        public sealed class Entry
        {
            public string Device;
            public string Description;
            public List<PHYSICAL_MONITOR> Physicals = new List<PHYSICAL_MONITOR>();
            public uint Count;
        }

        public static List<Entry> Enumerate()
        {
            var list = new List<Entry>();
            EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, delegate (IntPtr h, IntPtr hdc, ref RECT r, IntPtr d)
            {
                var mi = new MONITORINFOEX { cbSize = Marshal.SizeOf(typeof(MONITORINFOEX)) };
                GetMonitorInfo(h, ref mi);
                uint n = 0;
                if (!GetNumberOfPhysicalMonitorsFromHMONITOR(h, ref n) || n == 0) return true;
                var arr = new PHYSICAL_MONITOR[n];
                if (!GetPhysicalMonitorsFromHMONITOR(h, n, arr)) return true;
                var e = new Entry { Device = mi.szDevice, Count = n };
                foreach (var p in arr)
                {
                    e.Physicals.Add(p);
                    if (string.IsNullOrEmpty(e.Description)) e.Description = p.szPhysicalMonitorDescription;
                }
                list.Add(e);
                return true;
            }, IntPtr.Zero);
            return list;
        }

        public static void Close(Entry e)
        {
            if (e == null || e.Physicals.Count == 0) return;
            try { DestroyPhysicalMonitors(e.Count, e.Physicals.ToArray()); } catch { }
        }

        public static bool Read(IntPtr h, out uint value, out uint max, out int err)
        {
            uint v = 0, m = 0;
            bool ok = false;
            err = 0;
            for (int i = 0; i < 3; i++)
            {
                ok = GetVCPFeatureAndVCPFeatureReply(h, 0x60, IntPtr.Zero, ref v, ref m);
                if (ok) break;
                err = Marshal.GetLastWin32Error();
                Thread.Sleep(60 * (i + 1));
            }
            value = v; max = m;
            return ok;
        }

        public static bool Write(IntPtr h, uint value, out int err)
        {
            bool ok = false;
            err = 0;
            for (int i = 0; i < 3; i++)
            {
                ok = SetVCPFeature(h, 0x60, value);
                if (ok) return true;
                err = Marshal.GetLastWin32Error();
                Thread.Sleep(60 * (i + 1));
            }
            return ok;
        }
    }

    // ===================== 主程序 =====================
    internal static class Program
    {
        private const string Version = "3.4";
        private static readonly uint[] Codes = { 0x11, 0x12, 0x0F, 0x10 };
        private static readonly string[] Names = { "HDMI1", "HDMI2", "DP1", "DP2" };
        private static bool _verbose;
        private static string _libPath;
        private static int _forceIndex = -1;

        private static int Main(string[] args)
        {
            try
            {
                string cmd = null;
                for (int i = 0; i < args.Length; i++)
                {
                    string a = args[i];
                    if ((a == "--lib" || a == "/lib") && i + 1 < args.Length) _libPath = args[++i];
                    else if ((a == "--index" || a == "/index") && i + 1 < args.Length) { int t; if (int.TryParse(args[++i], out t)) _forceIndex = t; }
                    else if (cmd == null) cmd = a.Trim().ToLowerInvariant();
                }

                if (cmd == "help" || cmd == "-h" || cmd == "--help" || cmd == "/?" || cmd == "?")
                {
                    bool w = EnsureConsole();
                    Help();
                    if (w) WaitEnter();
                    return 0;
                }

                // DDCHelperLib.dll 只从运行目录取；缺失时才显示窗口提示
                if (!LibPresent())
                {
                    bool w = EnsureConsole();
                    Console.WriteLine("当前目录中无 DDCHelperLib.dll，请复制到当前目录。");
                    Console.WriteLine("下载地址http://list.wgpro.com/code/aoc");
                    Console.WriteLine();
                    Console.WriteLine("程序目录: " + AppDomain.CurrentDomain.BaseDirectory);
                    if (w) WaitEnter();
                    return 2;
                }

                // 无参数 = 静默自动切换，不创建任何窗口
                if (cmd == null) return Auto();

                switch (cmd)
                {
                    case "diag":
                        { bool w = EnsureConsole(); try { return Diag(); } finally { if (w) WaitEnter(); } }
                    case "list":
                        { bool w = EnsureConsole(); try { return List(); } finally { if (w) WaitEnter(); } }
                    case "1": return Switch(0);
                    case "2": return Switch(1);
                    case "3": return Switch(2);
                    case "4": return Switch(3);
                    case "debug1":
                        { bool w = EnsureConsole(); try { _verbose = true; return Switch(0); } finally { if (w) WaitEnter(); } }
                    case "debug2":
                        { bool w = EnsureConsole(); try { _verbose = true; return Switch(1); } finally { if (w) WaitEnter(); } }
                    case "debug3":
                        { bool w = EnsureConsole(); try { _verbose = true; return Switch(2); } finally { if (w) WaitEnter(); } }
                    case "debug4":
                        { bool w = EnsureConsole(); try { _verbose = true; return Switch(3); } finally { if (w) WaitEnter(); } }
                    case "debugauto":
                        { bool w = EnsureConsole(); try { _verbose = true; return Auto(); } finally { if (w) WaitEnter(); } }
                    default:
                        {
                            bool w = EnsureConsole();
                            Console.Error.WriteLine("参数无效: " + cmd);
                            Help();
                            if (w) WaitEnter();
                            return 1;
                        }
                }
            }
            catch (Exception ex)
            {
                if (_verbose) { EnsureConsole(); Console.Error.WriteLine("异常: " + ex.Message); }
                return 3;
            }
        }

        // ---------- 控制台有/无 ----------
        // 程序编译为 winexe：平时不出现黑色控制台；
        // 需要输出时（缺 DLL / diag / debug / help）才附加父控制台，或新建一个。
        [DllImport("kernel32.dll")] private static extern IntPtr GetConsoleWindow();
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool AttachConsole(uint pid);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool AllocConsole();

        private static bool _consoleInit;
        private static bool _allocatedConsole;

        // 返回 true 表示本次新建了独立控制台窗口（资源管理器双击等无父控制台场景）
        private static bool EnsureConsole()
        {
            if (_consoleInit) return _allocatedConsole;
            _consoleInit = true;
            try
            {
                if (GetConsoleWindow() == IntPtr.Zero)
                {
                    if (!AttachConsole(0xFFFFFFFF)) { AllocConsole(); _allocatedConsole = true; }
                }
                try { Console.OutputEncoding = Encoding.UTF8; } catch { }
                var w = new StreamWriter(Console.OpenStandardOutput(), Encoding.UTF8) { AutoFlush = true };
                Console.SetOut(w);
                Console.SetError(w);
                try { Console.SetIn(new StreamReader(Console.OpenStandardInput(), Encoding.UTF8)); } catch { }
            }
            catch { }
            return _allocatedConsole;
        }

        private static bool LibPresent()
        {
            if (!string.IsNullOrEmpty(_libPath)) return File.Exists(_libPath);
            return File.Exists(DdcHelper.DefaultPath);
        }

        private static void WaitEnter()
        {
            Console.Write("输入回车结束窗口...");
            try { Console.ReadLine(); } catch { }
        }

        // ---------- 切换：DDCHelper → NVAPI → dxva2 ----------
        private static int Switch(int index)
        {
            ushort target = (ushort)Codes[index];

            DdcHelper ddc; int idx;
            if (TryOpenDdcHelper(out ddc, out idx))
            {
                var v = ddc.NewSet(idx, 0x60, target);
                int rc = ddc.Set(ref v);
                if (_verbose) Console.WriteLine("[通道 A] {0}  index={1}  写入 rc=0x{2:X8}", ddc.LibPath, idx, rc);
                if (rc == 0)
                {
                    bool confirmed = ConfirmDdc(ddc, idx, target);
                    if (_verbose) Console.WriteLine(confirmed ? "[通道 A] 已确认切换" : "[通道 A] 写入已 ACK（回读未确认）");
                    return 0;
                }
            }
            else if (_verbose) Console.WriteLine("[通道 A] 不可用（无可用索引）");

            NvApi nv = NvApi.TryCreate();
            if (nv.Ready)
            {
                foreach (IntPtr gpu in nv.Gpus)
                {
                    foreach (uint mask in nv.Masks)
                    {
                        int rc = nv.Write(gpu, mask, target);
                        if (_verbose) Console.WriteLine("[通道 B] NVAPI mask=0x{0:X} 写入 rc=0x{1:X8}", mask, rc);
                        if (rc == 0) return 0;
                    }
                }
            }
            else if (_verbose) Console.WriteLine("[通道 B] NVAPI 不可用: {0}", nv.Detail);

            int r = Dxva2Switch((uint)target);
            if (_verbose && r == 0) Console.WriteLine("[通道 C] dxva2 写入成功");
            return r;
        }

        private static int Auto()
        {
            DdcHelper ddc; int idx;
            if (TryOpenDdcHelper(out ddc, out idx))
            {
                var g = ddc.NewGet(idx, 0x60);
                if (ddc.Get(ref g) == 0)
                {
                    ushort cur = (ushort)g.Result;
                    ushort t = NextTarget(cur);
                    if (_verbose) Console.WriteLine("[通道 A] 当前 0x{0:X4}({1}) → 0x{2:X4}({3})", cur, SourceName((byte)cur), t, SourceName((byte)t));
                    var s = ddc.NewSet(idx, 0x60, t);
                    if (ddc.Set(ref s) == 0) { ConfirmDdc(ddc, idx, t); return 0; }
                }
            }

            NvApi nv = NvApi.TryCreate();
            if (nv.Ready)
            {
                foreach (IntPtr gpu in nv.Gpus)
                {
                    foreach (uint mask in nv.Masks)
                    {
                        ushort cur; int rc;
                        if (!nv.TryRead(gpu, mask, out cur, out rc)) continue;
                        ushort t = NextTarget(cur);
                        if (_verbose) Console.WriteLine("[通道 B] 当前 0x{0:X4}({1}) → 0x{2:X4}({3})", cur, SourceName((byte)cur), t, SourceName((byte)t));
                        if (nv.Write(gpu, mask, t) == 0) return 0;
                    }
                }
            }

            List<Dxva2.Entry> list = Dxva2.Enumerate();
            foreach (Dxva2.Entry e in Order(list))
            {
                try
                {
                    if (e.Physicals.Count == 0) continue;
                    uint v, m; int err;
                    if (!Dxva2.Read(e.Physicals[0].hPhysicalMonitor, out v, out m, out err)) continue;
                    uint t = NextTarget((ushort)v);
                    if (Dxva2.Write(e.Physicals[0].hPhysicalMonitor, t, out err)) return 0;
                }
                finally { Dxva2.Close(e); }
            }
            return 3;
        }

        private static ushort NextTarget(ushort cur)
        {
            if (cur == 0x12) return 0x0F;
            if (cur == 0x0F) return 0x12;
            return 0x0F;
        }

        private static int Dxva2Switch(uint target)
        {
            foreach (Dxva2.Entry e in Order(Dxva2.Enumerate()))
            {
                try
                {
                    if (e.Physicals.Count == 0) continue;
                    int err;
                    if (Dxva2.Write(e.Physicals[0].hPhysicalMonitor, target, out err)) return 0;
                    if (_verbose) Console.WriteLine("[通道 C] {0} 写入失败 Win32 {1} (0x{1:X8}) {2}", e.Device, err, new System.ComponentModel.Win32Exception(err).Message);
                }
                finally { Dxva2.Close(e); }
            }
            return 3;
        }

        private static List<Dxva2.Entry> Order(List<Dxva2.Entry> all)
        {
            var aoc = new List<Dxva2.Entry>();
            var rest = new List<Dxva2.Entry>();
            foreach (Dxva2.Entry e in all)
            {
                string s = ((e.Description ?? "") + " " + (e.Device ?? "")).ToUpperInvariant();
                if (s.Contains("AOC") || s.Contains("AGON") || s.Contains("AG27")) aoc.Add(e); else rest.Add(e);
            }
            aoc.AddRange(rest);
            return aoc;
        }

        // 语义（与 Zeasn.DDC.Lib.vpf1gYOf0 反编译一致）：
        //   函数返回 rc==0 表示成功；DdcValue.Result = 读到的当前值；DdcValue.Value = VCP 最大值
        private static bool TryOpenDdcHelper(out DdcHelper ddc, out int index)
        {
            ddc = DdcHelper.Load(_libPath);
            index = -1;
            if (ddc == null)
            {
                if (_verbose) Console.WriteLine("[通道 A] 库加载失败: {0}", _libPath ?? DdcHelper.DefaultPath);
                return false;
            }
            if (_verbose) Console.WriteLine("[通道 A] 库: {0}", ddc.LibPath);

            int rcInit;
            try { rcInit = ddc.Init(); }
            catch (Exception ex)
            {
                if (_verbose) Console.WriteLine("[通道 A] Init 异常: {0}", ex.Message);
                return false;
            }
            if (_verbose) Console.WriteLine("[通道 A] DDCCIHelperIni=0x{0:X8}", rcInit);
            if (rcInit != 0) return false;

            // EnumDisplayIDIni 是 DDCHelperLib 的显示器枚举初始化步骤；
            // 缺了它，随后的 getStandardDDCCIValue_C 会直接失败。
            if (ddc.EnumDisplays != null)
            {
                try
                {
                    var ids = new EnumDisplayId { Ids = new int[520] };
                    int rcEnum = ddc.EnumDisplays(ref ids);
                    if (_verbose) Console.WriteLine("[通道 A] EnumDisplayIDIni=0x{0:X8} Name=\"{1}\" DefaultIndex={2}", rcEnum, ids.Name, ids.DefaultIndex);
                }
                catch (Exception ex) { if (_verbose) Console.WriteLine("[通道 A] EnumDisplayIDIni 异常: {0}", ex.Message); }
            }

            if (_forceIndex >= 0)
            {
                try
                {
                    var f = ddc.NewGet(_forceIndex, 0x60);
                    int rf = ddc.Get(ref f);
                    if (_verbose) Console.WriteLine("[通道 A] --index {0}: rc=0x{1:X8}", _forceIndex, rf);
                    if (rf == 0) { index = _forceIndex; return true; }
                }
                catch { }
                return false;
            }

            for (int i = 0; i <= 7; i++)
            {
                try
                {
                    var v = ddc.NewGet(i, 0x60);
                    int rc = ddc.Get(ref v);
                    if (_verbose) Console.WriteLine("[通道 A] index {0}: rc=0x{1:X8} 当前值=0x{2:X4}", i, rc, v.Result);
                    if (rc == 0) { index = i; return true; }
                }
                catch (Exception ex)
                {
                    if (_verbose) Console.WriteLine("[通道 A] index {0} 异常: {1}", i, ex.Message);
                    return false;
                }
            }
            return false;
        }

        private static bool ConfirmDdc(DdcHelper ddc, int index, ushort target)
        {
            for (int i = 0; i < 8; i++)
            {
                Thread.Sleep(250);
                try
                {
                    var v = ddc.NewGet(index, 0x60);
                    if (ddc.Get(ref v) == 0 && (ushort)v.Result == target) return true;
                }
                catch { return false; }
            }
            return false;
        }

        // ---------- diag / list ----------
        private static int Diag()
        {
            Console.WriteLine("=========== AOC 输入源切换诊断 v{0}（只读）===========", Version);
            Console.WriteLine("会话 {0} | 进程 {1} 位 | 用户 {2}", Process.GetCurrentProcess().SessionId, IntPtr.Size * 8, Environment.UserName);
            Console.WriteLine();

            Console.WriteLine("--- 通道 A：DDCHelperLib（G-Menu 同款）---");
            DdcHelper d = DdcHelper.Load(_libPath);
            if (d == null)
            {
                Console.WriteLine("[加载失败] {0}", _libPath ?? DdcHelper.DefaultPath);
            }
            else
            {
                Console.WriteLine("[已加载] {0}", d.LibPath);
                Console.WriteLine("DDCCIHelperIni() -> 0x{0:X8}", d.Init());
                var ids = new EnumDisplayId { Ids = new int[520] };
                int rcEnum = d.EnumDisplays == null ? -1 : d.EnumDisplays(ref ids);
                Console.WriteLine("EnumDisplayIDIni() -> 0x{0:X8}  Name=\"{1}\" DefaultIndex={2}", rcEnum, ids.Name, ids.DefaultIndex);
                for (int i = 0; i <= 7; i++)
                {
                    var v = d.NewGet(i, 0x60);
                    int rc = d.Get(ref v);
                    bool ok = rc == 0;
                    Console.WriteLine("  index {0}: rc=0x{1:X8} 当前值=0x{2:X4}{3} 最大值=0x{4:X4} {5}",
                        i, rc, v.Result, ok ? " (" + SourceName((byte)v.Result) + ")" : "", v.Value, ok ? "<== 可用" : "");
                }
                string log = d.ReadLog();
                if (!string.IsNullOrEmpty(log))
                {
                    Console.WriteLine("  -- DDCHelperLib 日志 --");
                    foreach (string line in log.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                        if (line.Trim().Length > 0) Console.WriteLine("  | " + line.Trim());
                }
            }

            Console.WriteLine();
            Console.WriteLine("--- 通道 B：NVAPI（NVIDIA 独显 I2C，系统级）---");
            NvApi nv = NvApi.TryCreate();
            Console.WriteLine("Ready={0} {1}", nv.Ready, nv.Detail);
            if (nv.Ready)
            {
                Console.WriteLine("GPU 数={0}  displayMask 候选={1}  GetConnectedDisplayIds=0x{2:X8}", nv.Gpus.Count, Join(nv.Masks), nv.DisplayIdsStatus);
                foreach (IntPtr gpu in nv.Gpus)
                {
                    foreach (uint mask in nv.Masks)
                    {
                        ushort v; int rc;
                        if (nv.TryRead(gpu, mask, out v, out rc))
                            Console.WriteLine("  mask=0x{0:X} VCP 0x60 = 0x{1:X4} ({2})", mask, v, SourceName((byte)v));
                        else
                            Console.WriteLine("  mask=0x{0:X} 读取失败 rc=0x{1:X8}", mask, rc);
                    }
                }
            }

            Console.WriteLine();
            Console.WriteLine("--- 通道 C：dxva2 / MCCS（new-ddc 同款）---");
            List<Dxva2.Entry> list = Dxva2.Enumerate();
            if (list.Count == 0) Console.WriteLine("未枚举到物理监视器");
            foreach (Dxva2.Entry e in list)
            {
                try
                {
                    Console.WriteLine("{0} | {1} | 物理监视器 {2}", e.Device, e.Description, e.Count);
                    if (e.Physicals.Count == 0) continue;
                    uint v, m; int err;
                    if (Dxva2.Read(e.Physicals[0].hPhysicalMonitor, out v, out m, out err))
                        Console.WriteLine("   VCP 0x60 = 0x{0:X4} ({1}) max=0x{2:X}", v, SourceName((byte)v), m);
                    else
                        Console.WriteLine("   VCP 0x60 读取失败 Win32 {0} (0x{0:X8}) {1}", err, new System.ComponentModel.Win32Exception(err).Message);
                }
                finally { Dxva2.Close(e); }
            }
            Console.WriteLine();
            Console.WriteLine("=========== 诊断结束 ===========");
            return 0;
        }

        private static string Join(List<uint> masks)
        {
            var sb = new StringBuilder();
            foreach (uint m in masks) sb.Append("0x").Append(m.ToString("X")).Append(' ');
            return sb.ToString().Trim();
        }

        private static int List()
        {
            DdcHelper d = DdcHelper.Load(_libPath);
            if (d != null)
            {
                d.Init();
                var ids = new EnumDisplayId { Ids = new int[520] };
                int rc = d.EnumDisplays == null ? -1 : d.EnumDisplays(ref ids);
                Console.WriteLine("通道 A（{0}）: EnumDisplayIDIni=0x{1:X8} Name=\"{2}\" DefaultIndex={3}", d.LibPath, rc, ids.Name, ids.DefaultIndex);
            }
            else Console.WriteLine("通道 A: 加载失败（{0}）", _libPath ?? DdcHelper.DefaultPath);

            NvApi nv = NvApi.TryCreate();
            Console.WriteLine("通道 B（NVAPI）: Ready={0} GPU={1} masks={2} {3}", nv.Ready, nv.Gpus.Count, Join(nv.Masks), nv.Detail);

            List<Dxva2.Entry> list = Dxva2.Enumerate();
            Console.WriteLine("通道 C（dxva2）: {0} 个物理监视器", list.Count);
            int i = 1;
            foreach (Dxva2.Entry e in list)
            {
                Console.WriteLine("  [{0}] {1} | {2}", i++, e.Device, e.Description);
                Dxva2.Close(e);
            }
            return 0;
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

        private static void Help()
        {
            Console.WriteLine("aoc-switch.exe v{0} —— AOC AG275UXM2 输入源切换（DDCHelper + NVAPI + dxva2）", Version);
            Console.WriteLine();
            Console.WriteLine("  aoc-switch.exe 1|2|3|4     静默切换 1=HDMI1 2=HDMI2 3=DP1 4=DP2");
            Console.WriteLine("  aoc-switch.exe             静默自动切换（0x12↔0x0F，其它→0x0F）");
            Console.WriteLine("  aoc-switch.exe diag        完整诊断（只读）");
            Console.WriteLine("  aoc-switch.exe list        列出通道与显示器");
            Console.WriteLine("  aoc-switch.exe debug1..4   带输出切换");
            Console.WriteLine("  aoc-switch.exe help");
            Console.WriteLine();
            Console.WriteLine("  DDCHelperLib.dll 须与本程序放在同一目录（下载: http://list.wgpro.com/code/aoc）");
            Console.WriteLine("  调试可选: --lib \"<路径>\\DDCHelperLib.dll\"   --index N");
            Console.WriteLine();
            Console.WriteLine("退出码: 0=成功 1=参数错误 2=未找到 DDCHelperLib.dll / 显示器 3=切换失败");
        }
    }
}

// AOC AGON 显示器输入源切换工具（纯 USB‑HID / MI_02 Realtek USB‑I2C 桥，官方 RhHidAPI.dll）
// 逆向自 G-Menu 的 Zeasn.Monitor.Lib.dll + RhHidAPI.dll，仅用于自有设备的互操作。
//
// 【通道定论】（真机 PowerShell + G-Menu .NET 反编译 + EneEc.dll 二次反汇编 三重证实）：
//   目标设备 VID_05F6 / PID_AC2F（"AGON3 AG275UXM Light FX Device"，串号 9876543210）
//   MI_02 = HID 类 / Realtek USB‑I2C 桥 —— G-Menu 切换信号源的实际通道！
//     通信链：RH_ConfigLog(true,0) → RH_ListAllHidDev 枚举 → RH_OpenHid(dwInst) → RH_HidI2CConfig(0x6E,1,0) → RH_HidI2CWrite/Read(0x51, ...)
//     寄存器=0x51（0x0051），读长=32，读前 buf[0]=0x6F
//   MI_00 = USBDevice 类 / Service=WINUSB / winusb.inf —— EneEc.dll 是 "USB -> SPI Flash Writer"，
//     为 SPI Flash 写入通道，NOT 信号源切换通道（0.6.6 及此前全走此通道，均无 DDC 应答）。
//
// DDC/CI 帧（VCP 0x60 输入源，完整软件帧，XOR 校验；实际下发去掉前 2 字节 6E 51）：
//   切换: 6E 51 84 03 60 <Hi> <Lo> <CHK>   查询: 6E 51 82 01 60 <CHK>
//   输入源编码（G-Menu StandardSource_E 枚举 + test 自检双重证实）：
//     DP1=0x0F  DP2=0x10  HDMI1=0x11  HDMI2=0x12
//
// 部署：exe 与 RhHidAPI.dll 必须同目录（RhHidAPI.dll 为 32 位原生库）；须 x86 编译（csc /platform:x86）。
// 编译：C:\Windows\Microsoft.NET\Framework\v4.0.30319\csc.exe /platform:x86 /out:AocInputSwitcher.exe Program.cs

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Microsoft.Win32;

namespace AocInputSwitcher
{
    // SetupDi 设备枚举通用结构
    [StructLayout(LayoutKind.Sequential)]
    internal struct SpDevInfoData
    {
        public int CbSize;
        public Guid ClassGuid;
        public uint DevInst;
        public IntPtr Reserved;
    }


    // 候选设备节点（由 SetupAPI 设备枚举，供 list 命令与 HID 通道枚举节点使用）
    internal sealed class DevNode
    {
        public uint DevInst;
        public string DeviceId;    // SetupAPI 设备实例 ID
    }

    internal static class Native
    {
        // ========== 通用常量 ==========
        public static readonly IntPtr INVALID_HANDLE = new IntPtr(-1);
        public const uint DIGCF_PRESENT = 0x02;
        public const uint DIGCF_ALLCLASSES = 0x04;

        // ========== SetupAPI（设备枚举） ==========
        private const string Setup = "setupapi.dll";

        [DllImport(Setup, CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern IntPtr SetupDiGetClassDevs(IntPtr classGuid, IntPtr enumerator, IntPtr hwnd, uint flags);

        [DllImport(Setup, CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern bool SetupDiEnumDeviceInfo(IntPtr hDevInfo, uint memberIndex, ref SpDevInfoData data);

        [DllImport(Setup, CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern bool SetupDiGetDeviceInstanceId(IntPtr hDevInfo, ref SpDevInfoData data,
            StringBuilder buffer, uint size, IntPtr requiredSize);

        [DllImport(Setup, SetLastError = true)]
        public static extern bool SetupDiDestroyDeviceInfoList(IntPtr hDevInfo);

        // ========== RhHidAPI.dll（G-Menu 官方原生库，MI_02 Realtek USB‑I2C 桥；32 位，须与 exe 同目录） ==========
        private const string RhHidApi = "RhHidAPI.dll";

        // 打开设备：入参为 RhHidAPI 内部设备索引（RH_ListAllHidDev 枚举得到的 Device_Info.dwInst，uint），
        // 传字符串会因参数截断导致打开失败（真机验证确认的根因）
        [DllImport(RhHidApi, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern IntPtr RH_OpenHid(uint dwDevInst);

        // 枚举本机全部 HID 设备：写入 Device_Info 数组指针与数量，返回错误码（0=成功）
        [DllImport(RhHidApi, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern int RH_ListAllHidDev(ref IntPtr ppDevListInfo, ref uint dwCount);

        // 释放 RH_ListAllHidDev 返回的设备数组（必须与枚举成对调用）
        [DllImport(RhHidApi, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern int RH_FreeDevList(IntPtr ppDevListInfo);

        // 日志开关（G-Menu 在枚举前调用 RH_ConfigLog(true, 0)）
        [DllImport(RhHidApi, CallingConvention = CallingConvention.StdCall, CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern bool RH_ConfigLog(bool bEnableLog, int nLevel);

        // 关闭设备
        [DllImport(RhHidApi, CallingConvention = CallingConvention.Cdecl, SetLastError = true)]
        public static extern void RH_CloseDev(IntPtr pDev);

        // 配置 I2C 事务：slave 从设备地址 / memLen 记忆长度 / speed 速度(0=默认)
        [DllImport(RhHidApi, CallingConvention = CallingConvention.StdCall, SetLastError = true)]
        public static extern int RH_HidI2CConfig(IntPtr pDev, byte slave, byte memLen, byte speed);

        // I2C 写：dwAddr 寄存器地址（0x0051），写入 buf[0..len)
        [DllImport(RhHidApi, CallingConvention = CallingConvention.StdCall, SetLastError = true)]
        public static extern int RH_HidI2CWrite(IntPtr pDev, uint dwAddr, byte[] buf, ushort len);

        // I2C 读：dwAddr 寄存器地址（0x0051），读入 buf（须 buf[0]=0x6F 帧头标记）
        [DllImport(RhHidApi, CallingConvention = CallingConvention.StdCall, SetLastError = true)]
        public static extern int RH_HidI2CRead(IntPtr pDev, uint dwAddr, byte[] buf, ushort len);
    }

    // RhHidAPI 设备信息结构体（与 G-Menu 反编译的 RTKRTKApi.Device_Info 逐字段一致）
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct Device_Info
    {
        // RhHidAPI 内部设备索引，作为 RH_OpenHid 入参
        public uint dwInst;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 1024)]
        public string hwID;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 1024)]
        public string Path;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 5)]
        public string PID;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 5)]
        public string VID;
        public int nLevel;
        public int nPort;
        public DEV_TYPE DevType;
        public DEV_SPEED UsbSpeed;
    }

    // 设备类型（与 G-Menu 反编译一致）
    [Flags]
    internal enum DEV_TYPE : uint
    {
        UnknownType = 0,
        BillBoard = 1,
        USB2Hub = 2,
        USB3Hub = 3,
        HID = 4
    }

    // USB 速度（与 G-Menu 反编译一致）
    [Flags]
    internal enum DEV_SPEED : uint
    {
        USBLowSpeed = 0,
        USBFullSpeed = 1,
        USBHighSpeed = 2,
        USBSuperSpeed = 3,
        UnknowSpeed = 4
    }

    internal interface II2cChannel
    {
        string Name { get; }
        string Describe { get; }
        bool TryOpen(string vid, string pid, bool verbose, out string info);
        bool RawWrite(byte[] payload, int len);
        bool RawRead(byte[] buf, int len);
        void Dispose();
    }

    internal static class UsbUtil
    {
        public static string Norm(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var sb = new StringBuilder();
            foreach (char c in s.Trim().ToUpperInvariant())
                if ((c >= '0' && c <= '9') || (c >= 'A' && c <= 'Z')) sb.Append(c);
            return sb.ToString();
        }

        public static bool MatchVidPid(string s, string vid, string pid)
        {
            string n = Norm(s);
            return n.Contains("VID" + vid) && n.Contains("PID" + pid);
        }

        public static string Hex(byte[] b, int len)
        {
            if (b == null) return "";
            int n = Math.Min(len, b.Length);
            var sb = new StringBuilder(n * 3);
            for (int i = 0; i < n; i++) sb.Append(b[i].ToString("X2") + " ");
            return sb.ToString().TrimEnd();
        }

        // 枚举所有 present 设备节点，返回实例 ID 含 VID/PID 的节点（供 list 命令与 HID 通道枚举节点使用）
        public static List<DevNode> EnumerateDevNodes(string vid, string pid, bool verbose)
        {
            var result = new List<DevNode>();
            IntPtr h = Native.SetupDiGetClassDevs(IntPtr.Zero, IntPtr.Zero, IntPtr.Zero,
                Native.DIGCF_ALLCLASSES | Native.DIGCF_PRESENT);
            if (h == Native.INVALID_HANDLE) return result;
            int cbDev = Marshal.SizeOf(typeof(SpDevInfoData));
            try
            {
                for (uint i = 0; ; i++)
                {
                    var dev = new SpDevInfoData { CbSize = cbDev };
                    if (!Native.SetupDiEnumDeviceInfo(h, i, ref dev)) break;
                    var sb = new StringBuilder(512);
                    if (!Native.SetupDiGetDeviceInstanceId(h, ref dev, sb, (uint)sb.Capacity, IntPtr.Zero))
                        continue;
                    string id = sb.ToString();
                    if (!MatchVidPid(id, vid, pid)) continue;
                    if (!result.Exists(x => x.DevInst == dev.DevInst))
                        result.Add(new DevNode { DevInst = dev.DevInst, DeviceId = id });
                }
            }
            finally { Native.SetupDiDestroyDeviceInfoList(h); }
            return result;
        }

        // 越小越优先：MI_02（USB‑HID Realtek USB‑I2C 桥）最优先，其次其它 MI，父设备最后
        public static int ScoreHid(string id)
        {
            string u = (id ?? "").ToUpperInvariant();
            if (u.StartsWith("HID\\", StringComparison.Ordinal) && u.Contains("&MI_02")) return 0;
            if (u.Contains("&MI_02")) return 1;
            if (u.StartsWith("HID\\", StringComparison.Ordinal)) return 2;
            if (u.Contains("&MI_")) return 3;
            return 9;   // 父设备 usb#vid&pid#serial（无 MI，HID 桥打不开）
        }
    }

    // ============================================================
    // USB‑HID 私有协议通道（MI_02 Realtek USB‑I2C 桥，G-Menu 实际使用的信号源切换通道）
    //   复刻 G-Menu（RTKRTKApi）打开流程：
    //     RH_ConfigLog(true,0) → RH_ListAllHidDev → Device_Info[] 解析 →
    //     过滤(VID/PID) + ScoreHid(MI_02 优先) → RH_OpenHid(dwInst) → RH_HidI2CConfig(0x6E,1,0) → 探测 DDC
    //   收发：RH_HidI2CWrite/Read(0x51, ...)；读前 buf[0]=0x6F
    // ============================================================
    internal sealed class HidRawChannel : II2cChannel
    {
        private const int GET_SLEEP_MS = 150;
        private const int READ_LEN = 32;
        private const byte SLAVE_ADDR = 0x6E;
        private const byte REG_ADDR = 0x51;

        private IntPtr _pDev = IntPtr.Zero;
        private string _nodeId = "";
        private string _describe = "";

        public string Name { get { return "USB‑HID（MI_02 Realtek USB‑I2C 桥 / RhHidAPI）"; } }
        public string Describe { get { return _describe; } }

        public bool TryOpen(string vid, string pid, bool verbose, out string info)
        {
            info = null;
            return OpenByRtK(vid, pid, verbose, out info);
        }

        // 复刻 G-Menu（RTKRTKApi）枚举打开流程：枚举参数是 RhHidAPI 内部索引（uint），
        // 此前传入 SetupAPI 实例 ID 字符串导致真机 4 个节点 RH_OpenHid 全失败
        private bool OpenByRtK(string vid, string pid, bool verbose, out string info)
        {
            info = null;
            IntPtr listPtr = IntPtr.Zero;
            try
            {
                // 1) 日志开关（G-Menu 在枚举前调用 RH_ConfigLog(true, 0)）
                if (!Native.RH_ConfigLog(true, 0) && verbose)
                    Console.WriteLine("  警告: RH_ConfigLog 返回 false（继续尝试）");

                // 2) 枚举本机全部 HID 设备
                uint count = 0;
                int rc = Native.RH_ListAllHidDev(ref listPtr, ref count);
                if (listPtr == IntPtr.Zero || count == 0)
                {
                    info = "RH_ListAllHidDev 未枚举到任何 HID 设备（返回码=" + rc + "，数量=" + count + "）";
                    if (verbose) Console.WriteLine("  " + info);
                    return false;
                }
                if (verbose && rc != 0) Console.WriteLine("  警告: RH_ListAllHidDev 返回码={0}（继续）", rc);
                if (verbose) Console.WriteLine("  系统 HID 设备共 {0} 个，解析中...", count);

                // 3) 逐元素解析为 Device_Info 数组
                int itemSize = Marshal.SizeOf(typeof(Device_Info));
                var devices = new Device_Info[count];
                for (int i = 0; i < count; i++)
                    devices[i] = Marshal.PtrToStructure<Device_Info>(listPtr + i * itemSize);

                // 4) 过滤本设备 + 排序（MI_02 HID 桥最优先）
                var candidates = new List<Device_Info>();
                foreach (var d in devices)
                {
                    bool match = UsbUtil.MatchVidPid(d.hwID, vid, pid);
                    if (!match)
                    {
                        match = (d.VID != null && d.VID.Trim().ToUpperInvariant() == vid.ToUpperInvariant())
                            && (d.PID != null && d.PID.Trim().ToUpperInvariant() == pid.ToUpperInvariant());
                    }
                    if (!match) continue;
                    if (verbose)
                        Console.WriteLine("  命中节点: dwInst=0x{0:X}  DevType={1}  hwID={2}", d.dwInst, d.DevType, (d.hwID ?? ""));
                    candidates.Add(d);
                }

                if (candidates.Count == 0)
                {
                    info = string.Format("枚举到 {0} 个 HID 设备，但无匹配 VID_{1}/PID_{2} 的节点（设备未连接或 RhHidAPI 驱动未加载？）",
                        count, vid, pid);
                    if (verbose) Console.WriteLine("  " + info);
                    return false;
                }
                candidates.Sort(delegate (Device_Info a, Device_Info b)
                    { return UsbUtil.ScoreHid(a.hwID).CompareTo(UsbUtil.ScoreHid(b.hwID)); });

                // 5) 逐个 RH_OpenHid(dwInst) → 配置 → 探测 DDC，命中即锁定
                foreach (var d in candidates)
                {
                    if (verbose)
                        Console.WriteLine("\n  === 尝试 RhHidAPI 打开 ===  dwInst=0x{0:X}  {1}", d.dwInst, (d.hwID ?? ""));
                    IntPtr pDev = Native.RH_OpenHid(d.dwInst);
                    if (pDev == IntPtr.Zero)
                    {
                        if (verbose) Console.WriteLine("    RH_OpenHid 失败，跳过");
                        continue;
                    }
                    if (verbose) Console.WriteLine("    RH_OpenHid 成功，pDev=0x{0:X}", pDev.ToInt64());
                    if (Native.RH_HidI2CConfig(pDev, SLAVE_ADDR, 1, 0) != 0)
                    {
                        if (verbose) Console.WriteLine("    RH_HidI2CConfig 失败，err={0}，关闭换下一设备", Marshal.GetLastWin32Error());
                        try { Native.RH_CloseDev(pDev); } catch { }
                        continue;
                    }
                    if (verbose) Console.WriteLine("    RH_HidI2CConfig 成功");

                    // 6) 探测 DDC：发 Get VCP 0x60，读回扫描 6E/8F 帧头
                    if (ProbeDdc(pDev, verbose))
                    {
                        _pDev = pDev;
                        _nodeId = (d.hwID ?? "") + " (dwInst=0x" + d.dwInst.ToString("X") + ")";
                        _describe = "dwInst=0x" + d.dwInst.ToString("X") + "  slave=0x" + SLAVE_ADDR.ToString("X2") +
                            "  reg=0x" + REG_ADDR.ToString("X2");
                        info = "RH_OpenHid 成功 @ dwInst=0x" + d.dwInst.ToString("X") + " (" + (d.hwID ?? "") + ")";
                        if (verbose) Console.WriteLine("\n  >>> 锁定 USB‑HID DDC 通道（MI_02）：{0}", info);
                        return true;
                    }
                    if (verbose) Console.WriteLine("    打开/配置成功但无 DDC 应答，关闭并尝试下一设备");
                    try { Native.RH_CloseDev(pDev); } catch { }
                }
            }
            finally
            {
                // 设备表释放（与打开的通道句柄无关）
                if (listPtr != IntPtr.Zero) Native.RH_FreeDevList(listPtr);
            }
            info = "所有候选节点均无法建立 DDC/CI 通道（RH_OpenHid 失败、RH_HidI2CConfig 失败或无 6E/8F 应答）";
            if (verbose) Console.WriteLine("\n  " + info);
            return false;
        }

        // 发 Get VCP 0x60 探测：RH_HidI2CWrite(0x51, {82 01 60 DC})（去 6E 51 前缀）→ 读回扫描 6E/8F 帧头
        private bool ProbeDdc(IntPtr pDev, bool verbose)
        {
            byte[] getPayload = { 0x82, 0x01, 0x60, 0xDC };
            int wOk = Native.RH_HidI2CWrite(pDev, REG_ADDR, getPayload, (ushort)getPayload.Length);
            if (wOk != 0)
            {
                if (verbose) Console.WriteLine("    ProbeDdc 写失败，err={0}", wOk);
                return false;
            }
            Thread.Sleep(GET_SLEEP_MS);

            var buf = new byte[READ_LEN];
            buf[0] = 0x6F;   // RH_HidI2CRead 要求 buf[0]=0x6F 帧头标记
            int rOk = Native.RH_HidI2CRead(pDev, REG_ADDR, buf, (ushort)READ_LEN);
            if (rOk != 0)
            {
                if (verbose) Console.WriteLine("    ProbeDdc 读失败，err={0}", rOk);
                return false;
            }
            if (verbose) Console.WriteLine("    ProbeDdc 读回: " + UsbUtil.Hex(buf, READ_LEN));

            for (int i = 0; i + 1 < READ_LEN; i++)
            {
                if (buf[i] == 0x6E && (buf[i + 1] & 0x80) != 0)
                {
                    if (verbose) Console.WriteLine("    *** 检测到 DDC 应答帧 6E/8F（MI_02）***");
                    return true;
                }
            }
            return false;
        }

        // 写 DDC 帧载荷（已去前 2 字节 6E 51）
        public bool RawWrite(byte[] payload, int len)
        {
            return Native.RH_HidI2CWrite(_pDev, REG_ADDR, payload, (ushort)len) == 0;
        }

        // 读 DDC 应答：读前 buf[0]=0x6F
        public bool RawRead(byte[] buf, int len)
        {
            int n = Math.Min(len, READ_LEN);
            Array.Clear(buf, 0, n);
            buf[0] = 0x6F;   // RH_HidI2CRead 要求 buf[0]=0x6F 帧头标记
            return Native.RH_HidI2CRead(_pDev, REG_ADDR, buf, (ushort)n) == 0;
        }

        public void Dispose()
        {
            if (_pDev != IntPtr.Zero) { try { Native.RH_CloseDev(_pDev); } catch { } _pDev = IntPtr.Zero; }
        }
    }

    // ============================================================
    // 显示器控制（DDC/CI 帧，USB‑HID / MI_02 通道）
    // ============================================================
    internal sealed class Monitor : IDisposable
    {
        private const byte SLAVE_ADDR = 0x6E;
        private const byte REG_ADDR = 0x51;
        private const byte VCP_INPUT_SOURCE = 0x60;
        private const int IO_RETRY = 3;
        private const int GET_SLEEP_MS = 150;

        private readonly string _targetVid, _targetPid;
        private readonly bool _verbose;
        private II2cChannel _channel;

        public string ChannelName { get { return _channel != null ? _channel.Name : "无"; } }

        public Monitor(string vid, string pid, bool verbose)
        {
            _targetVid = (vid ?? "05F6").Trim().ToUpperInvariant();
            _targetPid = (pid ?? "AC2F").Trim().ToUpperInvariant();
            _verbose = verbose;
        }

        public void Open()
        {
            var ch = new HidRawChannel();
            Console.WriteLine("尝试通道：{0} ...", ch.Name);
            string info;
            bool ok = false;
            try { ok = ch.TryOpen(_targetVid, _targetPid, _verbose, out info); }
            catch (Exception ex) { info = "异常: " + ex.Message; ok = false; }

            if (!ok)
            {
                Console.WriteLine("  通道不可用：{0}", info);
                try { ch.Dispose(); } catch { }
                Console.WriteLine();
                Console.WriteLine("无法打开 USB‑HID DDC/CI 通道。请确认：");
                Console.WriteLine("  ① 显示器 USB 上行线已连接到本机，且在【接显示器的这台】电脑运行；");
                Console.WriteLine("  ② G-Menu 会独占 MI_02 通道——本工具运行前请完全退出 G-Menu（含后台托盘进程）；");
                Console.WriteLine("  ③ 运行 diag，把上方每个节点的 RH_OpenHid/RH_HidI2CConfig/RH_HidI2CWrite/Read 返回码整段反馈。");
                Console.WriteLine("  ④ 无显示器环境：可先运行 test（协议离线自检）或 list（枚举本机 USB 设备）。");
                throw new InvalidOperationException("device not found");
            }

            _channel = ch;
            Console.WriteLine("已连接设备（实测 DDC/CI 有响应）：{0}", info);
            if (!string.IsNullOrEmpty(ch.Describe)) Console.WriteLine("  " + ch.Describe);
        }

        // 构建 DDC/CI 完整软件帧（含 6E 51 前缀与 XOR 校验）；internal 供离线自检（test 命令）复用
        internal static byte[] BuildFrame(bool isGet, params byte[] commands)
        {
            int n = commands.Length;
            var f = new byte[n + 5];
            f[0] = SLAVE_ADDR; f[1] = REG_ADDR;
            f[2] = (byte)(0x80 + n + 1);
            f[3] = isGet ? (byte)0x01 : (byte)0x03;
            Array.Copy(commands, 0, f, 4, n);
            byte chk = 0;
            for (int i = 0; i < n + 4; i++) chk ^= f[i];
            f[n + 4] = chk;
            return f;
        }

        private bool RawWrite(byte[] frame)
        {
            int len = frame.Length - 2;
            var p = new byte[len];
            Array.Copy(frame, 2, p, 0, len);
            for (int attempt = 1; attempt <= IO_RETRY; attempt++)
            {
                if (_channel.RawWrite(p, len)) return true;
                Thread.Sleep(2 * attempt);
            }
            return false;
        }

        private bool RawRead(byte[] buf)
        {
            for (int attempt = 1; attempt <= IO_RETRY; attempt++)
            {
                if (_channel.RawRead(buf, buf.Length) && FindReplyHead(buf) >= 0) return true;
                Thread.Sleep(2 * attempt);
            }
            return false;
        }

        private static int FindReplyHead(byte[] b)
        {
            if (b == null || b.Length < 2) return -1;
            int lim = Math.Min(b.Length - 2, 16);
            for (int i = 0; i <= lim; i++)
                if (b[i] == 0x6E && (b[i + 1] & 0x80) != 0) return i;
            return -1;
        }

        public bool SetStandardVcp(byte vcp, ushort value)
        {
            return RawWrite(BuildFrame(false, vcp, (byte)(value >> 8), (byte)(value & 0xFF)));
        }

        public bool GetStandardVcp(byte vcp, out ushort value, out ushort max, out byte[] raw)
        {
            value = 0; max = 0; raw = null;
            byte[] frame = BuildFrame(true, vcp);
            for (int attempt = 0; attempt < IO_RETRY; attempt++)
            {
                if (!RawWrite(frame)) continue;
                Thread.Sleep(GET_SLEEP_MS);
                var buf = new byte[64];
                if (!RawRead(buf)) continue;
                raw = buf;
                int head = FindReplyHead(buf);
                if (head >= 0)
                {
                    if (head + 9 < buf.Length && buf[head + 2] == vcp)
                    {
                        max = (ushort)((buf[head + 5] << 8) | buf[head + 6]);
                        value = (ushort)((buf[head + 7] << 8) | buf[head + 8]);
                    }
                    else
                    {
                        int idx = buf[1] - 0x80;
                        if (idx >= 4 && idx + 5 < buf.Length)
                        {
                            max = (ushort)((buf[idx - 2] << 8) | buf[idx - 1]);
                            value = (ushort)((buf[idx] << 8) | buf[idx + 1]);
                        }
                    }
                    return true;
                }
            }
            return false;
        }

        public bool SetInputSource(byte v) { return SetStandardVcp(VCP_INPUT_SOURCE, v); }
        public bool GetInputSource(out byte value, out byte[] raw)
        {
            value = 0; raw = null;
            ushort v, max;
            if (GetStandardVcp(VCP_INPUT_SOURCE, out v, out max, out raw)) { value = (byte)(v & 0xFF); return true; }
            return false;
        }

        public void Dispose()
        {
            if (_channel != null) { try { _channel.Dispose(); } catch { } _channel = null; }
        }

        public static string SourceName(byte v)
        {
            switch (v)
            {
                case 0x0F: return "DisplayPort 1 (DP1)";
                case 0x10: return "DisplayPort 2 (DP2)";
                case 0x11: return "HDMI 1 (HDMI1)";
                case 0x12: return "HDMI 2 (HDMI2)";
                case 0x15: return "USB-C 1";
                case 0x16: return "USB-C 2";
                case 0x01: return "VGA 1";
                case 0x03: return "DVI 1";
                case 0x04: return "DVI 2";
                default: return "未知/其它 (0x" + v.ToString("X2") + ")";
            }
        }
    }

    internal static class Program
    {
        private static int Main(string[] args)
        {
            string vid = "05F6", pid = "AC2F";
            bool verbose = false;
            var positional = new List<string>();
            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i];
                if ((a == "--vid" || a == "/vid") && i + 1 < args.Length) vid = args[++i];
                else if ((a == "--pid" || a == "/pid") && i + 1 < args.Length) pid = args[++i];
                else if (a.StartsWith("--vid=", StringComparison.OrdinalIgnoreCase)) vid = a.Substring(6);
                else if (a.StartsWith("--pid=", StringComparison.OrdinalIgnoreCase)) pid = a.Substring(6);
                else if (a == "-v" || a == "--verbose" || a == "diag") { verbose = true; positional.Add(a); }
                else positional.Add(a.ToLowerInvariant());
            }

            Console.Title = "AOC AGON 输入源切换（纯 USB‑HID / MI_02 RhHidAPI）";
            Console.WriteLine("=== AOC AGON 显示器输入源切换 ===");
            Console.WriteLine("目标设备 VID_{0} / PID_{1}（MI_02 USB‑HID / Realtek USB‑I2C 桥，官方 RhHidAPI.dll）",
                vid.ToUpperInvariant(), pid.ToUpperInvariant());
            Console.WriteLine("进程位数：{0} 位（RhHidAPI.dll 为 32 位原生库，本 exe 须为 x86，且与 dll 同目录）", IntPtr.Size * 8);

            // 离线命令分流：test（协议自检）/ list（设备枚举）
            string earlyCmd = positional.Count > 0 ? positional[0].ToLowerInvariant() : null;
            if (earlyCmd == "test") return RunSelfTest();
            if (earlyCmd == "list") return RunDeviceList(vid, pid);

            using (var mon = new Monitor(vid, pid, verbose))
            {
                bool opened = false;
                try { mon.Open(); opened = true; }
                catch { opened = false; }
                if (!opened)
                {
                    Console.WriteLine("\n[重要] 请确认：①完全退出 G-Menu（含后台托盘进程）后重试；②必要时右键“以管理员身份运行”；③把以上输出反馈。");
                    if (positional.Count == 0) { Console.WriteLine("\n按回车退出。"); try { Console.ReadLine(); } catch { } }
                    return 2;
                }
                Console.WriteLine("设备就绪（DDC/CI slave=0x6E，通道 {0}）。\n", mon.ChannelName);

                if (positional.Count > 0)
                {
                    string cmd = positional[0].ToLowerInvariant();
                    if (cmd == "diag") return RunDiag(mon);
                    return RunCommand(mon, cmd);
                }

                while (true)
                {
                    Console.WriteLine("[当前输入源]");
                    Console.WriteLine("  1) 切换到 HDMI1");
                    Console.WriteLine("  2) 切换到 HDMI2");
                    Console.WriteLine("  3) 切换到 DP1");
                    Console.WriteLine("  4) 切换到 DP2");
                    Console.WriteLine("  5) 查询当前输入源");
                    Console.WriteLine("  d) 设备诊断信息");
                    Console.WriteLine("  0) 退出");
                    Console.Write("请选择: ");
                    string key;
                    try { key = Console.ReadLine(); } catch { break; }
                    if (key == null) break;
                    key = key.Trim().ToLowerInvariant();
                    Console.WriteLine();
                    if (key == "0" || key == "q") break;
                    if (key == "1") SwitchAndConfirm(mon, 0x11, "HDMI1");
                    else if (key == "2") SwitchAndConfirm(mon, 0x12, "HDMI2");
                    else if (key == "3") SwitchAndConfirm(mon, 0x0F, "DP1");
                    else if (key == "4") SwitchAndConfirm(mon, 0x10, "DP2");
                    else if (key == "5") QueryAndPrint(mon);
                    else if (key == "d") RunDiag(mon);
                    else Console.WriteLine("无效选择。");
                    Console.WriteLine();
                }
            }
            return 0;
        }

        private static int RunCommand(Monitor mon, string cmd)
        {
            switch (cmd)
            {
                case "hdmi1": return SwitchAndConfirm(mon, 0x11, "HDMI1") ? 0 : 1;
                case "hdmi2": return SwitchAndConfirm(mon, 0x12, "HDMI2") ? 0 : 1;
                case "dp1": return SwitchAndConfirm(mon, 0x0F, "DP1") ? 0 : 1;
                case "dp2": return SwitchAndConfirm(mon, 0x10, "DP2") ? 0 : 1;
                case "query":
                case "get": return QueryAndPrint(mon) ? 0 : 1;
                case "diag": return RunDiag(mon);
                default: Console.WriteLine("未知命令：" + cmd + "（支持 hdmi1/hdmi2/dp1/dp2/query/diag；离线命令 test/list 无需连接显示器）"); return 3;
            }
        }

        private static int RunDiag(Monitor mon)
        {
            Console.WriteLine("--- 设备诊断（通道 {0}）---", mon.ChannelName);
            Console.WriteLine("（发送一次查询以验证读写链路）");
            byte v; byte[] raw;
            if (mon.GetInputSource(out v, out raw))
            {
                Console.WriteLine("读写成功！当前输入源：" + Monitor.SourceName(v));
                Dump(raw);
                return 0;
            }
            Console.WriteLine("查询未成功（设备未在规定时间内响应 DDC/CI）。");
            if (raw != null) Dump(raw);
            Console.WriteLine("若设备已连接但查询失败，请把以上输出反馈。");
            return 1;
        }

        // 协议离线自检 - 构建全部 DDC/CI 输入源帧并重算 XOR 校验逐字节比对，
        // 纯 C# 字节运算、无需连接显示器
        private static int RunSelfTest()
        {
            Console.WriteLine("--- 协议离线自检（DDC/CI 帧构建 + XOR 校验）---");
            Console.WriteLine("纯 C# 字节运算，无需连接显示器。");
            Console.WriteLine();

            var cases = new[]
            {
                new { Name = "查询当前输入源  Get VCP 0x60",       Frame = Monitor.BuildFrame(true, 0x60),              Expect = "6E 51 82 01 60 DC" },
                new { Name = "切换到 DP1    Set VCP 0x60 = 0x0F",  Frame = Monitor.BuildFrame(false, 0x60, 0x00, 0x0F), Expect = "6E 51 84 03 60 00 0F D7" },
                new { Name = "切换到 DP2    Set VCP 0x60 = 0x10",  Frame = Monitor.BuildFrame(false, 0x60, 0x00, 0x10), Expect = "6E 51 84 03 60 00 10 C8" },
                new { Name = "切换到 HDMI1  Set VCP 0x60 = 0x11",  Frame = Monitor.BuildFrame(false, 0x60, 0x00, 0x11), Expect = "6E 51 84 03 60 00 11 C9" },
                new { Name = "切换到 HDMI2  Set VCP 0x60 = 0x12",  Frame = Monitor.BuildFrame(false, 0x60, 0x00, 0x12), Expect = "6E 51 84 03 60 00 12 CA" },
            };

            int pass = 0, fail = 0;
            foreach (var c in cases)
            {
                byte chk = 0;
                for (int i = 0; i < c.Frame.Length - 1; i++) chk ^= c.Frame[i];
                bool xorOk = chk == c.Frame[c.Frame.Length - 1];

                string hex = UsbUtil.Hex(c.Frame, c.Frame.Length);
                bool expectOk = hex == c.Expect;

                var payload = new byte[c.Frame.Length - 2];
                Array.Copy(c.Frame, 2, payload, 0, payload.Length);

                bool ok = xorOk && expectOk;
                if (ok) pass++; else fail++;
                Console.WriteLine("[{0}] {1}", ok ? "通过" : "失败", c.Name);
                Console.WriteLine("      完整帧  : {0}", hex);
                Console.WriteLine("      下发载荷: {0}", UsbUtil.Hex(payload, payload.Length));
                Console.WriteLine("      XOR 校验: 帧尾 {0} / 重算 {1}（{2}）",
                    c.Frame[c.Frame.Length - 1].ToString("X2"), chk.ToString("X2"), xorOk ? "一致" : "不一致!");
                if (!expectOk) Console.WriteLine("      【与官方帧不符】期望: {0}", c.Expect);
                Console.WriteLine();
            }

            Console.WriteLine("自检结果: {0} 通过 / {1} 失败", pass, fail);
            if (fail == 0)
            {
                Console.WriteLine("帧格式与 G-Menu 反编译结果逐字节一致，协议逻辑正确。");
                Console.WriteLine("连接显示器 USB 上行线后可执行: AocInputSwitcher.exe hdmi1 | hdmi2 | dp1 | dp2 | query | diag");
                return 0;
            }
            Console.WriteLine("存在失败用例，请检查 Monitor.BuildFrame 逻辑。");
            return 1;
        }

        // 本机设备枚举 - 用 SetupAPI 列出全部含 VID/PID 的 present 设备节点并高亮目标显示器，
        // 无需连接显示器（验证设备节点可见性）
        private static int RunDeviceList(string vid, string pid)
        {
            Console.WriteLine("--- 本机 USB 设备枚举（SetupAPI）---");
            Console.WriteLine("目标设备: VID_{0} / PID_{1}", vid.ToUpperInvariant(), pid.ToUpperInvariant());
            Console.WriteLine();

            var nodes = UsbUtil.EnumerateDevNodes(vid, pid, false);
            if (nodes.Count > 0)
            {
                Console.WriteLine("【已找到目标显示器】共 {0} 个节点:", nodes.Count);
                foreach (var n in nodes) Console.WriteLine("  >> DevInst={0}  {1}", n.DevInst, n.DeviceId);
                Console.WriteLine("可执行切换: AocInputSwitcher.exe hdmi1 | hdmi2 | dp1 | dp2");
            }
            else
            {
                Console.WriteLine("【未找到目标设备】VID_{0}/PID_{1} 不在本机。",
                    vid.ToUpperInvariant(), pid.ToUpperInvariant());
                Console.WriteLine("原因: 显示器 USB 上行线未连接本机（桥在显示器内部，随上行线挂载）。");
                Console.WriteLine("处理: 将显示器 USB 上行线连接到本机后重新运行本命令确认。");
            }
            return nodes.Count > 0 ? 0 : 1;
        }

        private static bool SwitchAndConfirm(Monitor mon, byte value, string name)
        {
            Console.WriteLine("切换 -> {0} (VCP 0x60 = 0x{1:X2}) ...", name, value);
            if (mon.SetInputSource(value))
            {
                Console.WriteLine("已发送切换命令。");
                Thread.Sleep(400);
                QueryAndPrint(mon);
                return true;
            }
            Console.WriteLine("切换失败（无 DDC/CI 应答）。");
            return false;
        }

        private static bool QueryAndPrint(Monitor mon)
        {
            Console.WriteLine("查询当前输入源 ...");
            byte v; byte[] raw;
            if (!mon.GetInputSource(out v, out raw))
            {
                Console.WriteLine("查询失败（设备未在规定时间内响应 DDC/CI）。");
                if (raw != null) Dump(raw);
                return false;
            }
            Console.WriteLine("当前输入源：" + Monitor.SourceName(v));
            Dump(raw);
            return true;
        }

        private static void Dump(byte[] b)
        {
            if (b == null) return;
            var sb = new StringBuilder("原始返回: ");
            for (int i = 0; i < b.Length; i++)
            {
                if (i > 0 && i % 16 == 0) sb.Append("\n          ");
                sb.Append(b[i].ToString("X2") + " ");
            }
            Console.WriteLine(sb.ToString());
        }
    }
}
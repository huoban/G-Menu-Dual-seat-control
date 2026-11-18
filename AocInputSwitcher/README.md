# AocInputSwitcher

独立命令行工具，用于通过 USB 切换 **AOC AGON AG275UXM**（27" MiniLED 4K，VID_05F6 / PID_AC2F）显示器的输入源（HDMI1 / HDMI2 / DP1 / DP2）。

协议从 G-Menu 反编译还原：.NET 层 `Zeasn.Monitor.Lib`（通用路径）+ ENE 厂商原生库 `EneEc.dll`（本机实际路径），确认为 **WinUSB rawusb 私有协议**，运行在 MI_00 厂商自定义接口（class 0xFF，控制传输，0 端点）上。

## 当前状态（v0.6.6，2026-10-16）

- **阶段**：根因已定位并修复，代码就绪，**待真机验证**。
- **根因**：本机 AG275UXM 的 USB 主控为 **ENE 芯片**（`USBViewAll.txt` iManufacturer="ENE"），G-Menu 分派 **ENE 私有路径**（`Zeasn.USB.ENE.Lib` + 原生 `EneEc.dll`）。反汇编 `EneEc.dll` 确认其 WinUSB 控制传输真实参数：写 `bmRequestType=0x40 bRequest=0x80`、读 `bmRequestType=0xC0 bRequest=0x81`，`reg` 32 位拆 `wValue`（地址 0x6E）/`wIndex`（寄存器 0x51）。此前误用通用路径 `Zeasn.Monitor.Lib` 的 bRequest（0xA3/0xB7/...），致设备对每次传输 STALL → `err=31`。
- **0.6.6 修复**：`WRITE_REQUESTS`/`READ_REQUESTS` 首项改 0x80/0x81（旧候选保留兜底）；`ControlTransfer` 加 reg 参数（`WValue=reg>>16`、`WIndex=reg&0xFFFF`）；`ProbeDdc` 加 reg 遍历；rawusb 新增「ENE 路径」探测块。
- **已完成 / 已验证**：编译通过（EXIT=0）；`test` 5 通过 / 0 失败；无显示器环境 `rawusb` 正常运行（正确报"未找到设备"，不崩溃）。
- **下一步（需你操作）**：在**接显示器的电脑**上（完全退出 G-Menu 后台进程含托盘后）运行 `rawusb`，重点看新增的「ENE 路径」块——期望 `>> OUT req=0x80 reg=0x6E0051 ... = True` 且 `<< IN req=0x81 ... 数据: 6E ...` 命中。命中后 `hdmi1/hdmi2/dp1/dp2/query` 即可切源。若 ENE 块失败（reg 编码非 0x6E0051），把 rawusb 完整输出反馈我，我据此调整 `REG_CANDIDATES` 候选顺序；仍无果则用 **Process Monitor** 抓 G-Menu 真实调用逐参数比对。

## 命令用法

```
AocInputSwitcher.exe hdmi1    # 切换到 HDMI1
AocInputSwitcher.exe hdmi2    # 切换到 HDMI2
AocInputSwitcher.exe dp1      # 切换到 DP1
AocInputSwitcher.exe dp2      # 切换到 DP2
AocInputSwitcher.exe query    # 查询当前输入源
AocInputSwitcher.exe rawusb   # WinUSB 通道诊断（设备枚举 / 节点状态 / 传输探测）
AocInputSwitcher.exe diag     # 综合诊断
AocInputSwitcher.exe test     # 协议离线自检（无需连接显示器）
```

编译（需 .NET Framework，系统自带 csc）：

```
C:\Windows\Microsoft.NET\Framework\v4.0.30319\csc.exe /out:AocInputSwitcher.exe Program.cs
```

## 设备通道结论

| 接口 | 类 | 驱动 | 用途 |
|------|-----|------|------|
| MI_00 | 0xFF（厂商自定义） | WINUSB | **输入源切换实际通道**（rawusb 私有协议，控制传输、0 端点） |
| MI_01 | HID | HidUsb | 输入传感器（本工具不用） |
| MI_02 | HID | HidUsb | 灯效 Light FX（本工具不用） |

MI_00 注册的是 winusb.inf 自定义 `DeviceInterfaceGUID = 6987b675-8cbc-4bd3-a557-c627cea223dc`，非标准 `GUID_DEVINTERFACE_USB_DEVICE`，故必须按该 GUID 或全接口扫描枚举，否则只会抓到 usbccgp 父设备。

## 排错过程记录

### 已解决

| # | 错误现象 | 根因 | 修复 |
|---|----------|------|------|
| 1 | `RH_OpenHid` 返回 NULL / 访问违例 | RhHidAPI.dll 的 `RH_OpenHid` 参数不是设备实例索引（uint），而是宽字符串完整实例 ID | 改传实例 ID；最终放弃 RhHidAPI 依赖 |
| 2 | `hid.dll` 无 `HidD_OpenDeviceW` 导出 | 系统 hid.dll 不存在该入口 | 纯 HID 直连方案不可行，转向 WinUSB |
| 3 | 所有 HID 接口 `err=2`（FILE_NOT_FOUND） | 系统级 HID 过滤驱动（Realtek 代理驱动）拦截 | 放弃 HID，改为 WinUSB rawusb 私有协议 |
| 4 | `WinUsb_Initialize` 返回 `err=6`（INVALID_HANDLE） | `CreateFile` 的 `dwFlagsAndAttributes=0`，而 G-Menu 反编译原值为 `0x40000000` | 0.6.3 对齐 G-Menu 原值，真机验证 WinUsb_Initialize 成功 |
| 5 | 路径 B 构造返回 `err=2`（FILE_NOT_FOUND） | 符号链接 `#{GUID}` 漏掉花括号 `{}` | 补 `{}`；且路径 B 仅作路径 A 无结果时兜底 |
| 6 | 诊断属性全空 | `Get-PnpDeviceProperty` 需要管理员权限 | 以管理员运行诊断命令 / 直读注册表 |

### 当前问题（0.6.6 已修复，待真机验证）

| 错误现象 | 关键事实 | 当前方向 |
|----------|----------|----------|
| `WinUsb_ControlTransfer` 所有候选（含 0.6.5 独立 IN 扫描）返回 `err=31` | ① err=31 = `ERROR_GEN_FAILURE`（设备 STALL，非参数不匹配）；② **根因（0.6.6 定位）**：本机 AG275UXM USB 主控为 **ENE 芯片**（`USBViewAll.txt` iManufacturer="ENE"），G-Menu 分派 **ENE 私有路径**（`Zeasn.USB.ENE.Lib` + 原生 `EneEc.dll`），其 WinUSB 控制传输 bRequest 写=**0x80**/读=**0x81**（反汇编 EneEc.dll writer 0x32C0 / reader 0x31F0 确认），reg 32 位拆 wValue(地址 0x6E)/wIndex(寄存器 0x51)；此前从通用路径 `Zeasn.Monitor.Lib` 逆出的 bRequest（0xA3/0xB7/...）对 ENE 芯片无效，设备对每次传输 STALL | 0.6.6 已改 ENE 真实 bRequest（0x80/0x81 为首选）+ reg 编码（首选 0x6E0051），rawusb 新增「ENE 路径」探测块验证；真机跑 `rawusb` 确认命中 6E/8F 后，`hdmi1/hdmi2/dp1/dp2/query` 即可切源 |

## 当前解决思路

1. **通道已打通**：MI_00 + CreateFile(WINUSB flag 0x40000000) + WinUsb_Initialize 均成功。
2. **根因定位（0.6.6）**：G-Menu 有两套 USB 路径。本机 AG275UXM 为 **ENE 芯片**（`USBViewAll.txt` iManufacturer="ENE"），G-Menu 分派 **ENE 私有路径**（`Zeasn.USB.ENE.Lib` + 原生 `EneEc.dll`）。反汇编 `EneEc.dll` 确认其 WinUSB 控制传输为：写 `bmRequestType=0x40 bRequest=0x80`、读 `bmRequestType=0xC0 bRequest=0x81`，且 `reg` 32 位拆 `wValue`（地址 0x6E）/`wIndex`（寄存器 0x51）。此前从通用路径 `Zeasn.Monitor.Lib` 逆出的 bRequest（0xA3/0xB7/...）对 ENE 芯片无效，故设备对每次传输 STALL（err=31）。
3. **0.6.6 修复**：`WRITE_REQUESTS`/`READ_REQUESTS` 首项改为 0x80/0x81（旧候选保留兜底），`ControlTransfer` 加 reg 参数（WValue=reg>>16、WIndex=reg&0xFFFF），`ProbeDdc` 加 reg 遍历，rawusb 新增「ENE 路径」探测块。
4. **后续步骤**：
   - 真机跑 `rawusb`，看「ENE 路径」块 OUT req=0x80 reg=0x6E0051 是否成功 + 读 req=0x81 命中 6E/8F；
   - 命中后 `hdmi1/hdmi2/dp1/dp2/query` 即可切源；
   - 若 reg 编码非 0x6E0051（ENE 块失败），按 rawusb 输出反馈调整 `REG_CANDIDATES` 候选顺序；仍无果则用 **Process Monitor** 抓 G-Menu 真实调用逐参数比对。

## 关键教训

- 访问设备前必须**完全退出 G-Menu 后台进程**（含托盘与后台服务），避免设备被独占。
- `pnputil /enum-drivers /class "USBDevice"` 对系统内置驱动（winusb.sys）**不会列出**，不可作为驱动是否绑定的依据；应直读注册表 `HKLM:\SYSTEM\CurrentControlSet\Enum\{实例ID}\Service`。
- `Get-PnpDeviceProperty` 等 PnP 诊断 API 需要**管理员权限**，否则静默返回空。
- 反编译代码高度混淆，`WValue` / `WIndex` / `bRequest` 等字段须跨多个 builder 交叉比对确认真实取值，单点推断易出错。

## 反编译产物与关键文件

### 反编译源码保存在哪里

G-Menu 是 .NET 程序，安装目录中的程序集已被反编译为 C# 源码，统一存放在 `d:\Ai\G-Menu\decompiled\`，按程序集分目录：

| 反编译源码 | 对应原程序集 | 说明 |
|-----------|-------------|------|
| `decompiled\Zeasn.Monitor.Lib\Zeasn.Monitor.Lib.decompiled.cs` | `GMeun\resources\bin\Zeasn.Monitor.Lib.dll` | **核心**：WinUSB/DDC/CI 输入源切换协议、`WinUsb_Initialize`/`WinUsb_ControlTransfer` 调用链 |
| `decompiled\Zeasn.Equipment.Entity.Lib\Zeasn.Equipment.Entity.Lib.decompiled.cs` | `GMeun\resources\bin\Zeasn.Equipment.Entity.Lib.dll` | 设备实体、输入源码点等枚举定义 |
| `decompiled\Zeasn.Framework.Core.Lib\Zeasn.Framework.Core.Lib.decompiled.cs` | `GMeun\resources\bin\Zeasn.Framework.Core.Lib.dll` | 框架基类 |
| `decompiled\G_Menu\G_Menu.decompiled.cs` | `GMeun\G-Menu.exe` | 主程序入口 |

原始 G-Menu 安装目录为 `d:\Ai\G-Menu\GMeun\`：入口 `G-Menu.exe`，Electron 前端资源 `resources\app.asar`，反编译所用的 .NET 程序集位于 `resources\bin\`。

### 需要留意的文件

| 文件 | 用途 / 为何需留意 |
|------|-------------------|
| `AocInputSwitcher\Program.cs` | 本工具唯一源码，含全部通信协议实现 |
| `decompiled\Zeasn.Monitor.Lib\Zeasn.Monitor.Lib.decompiled.cs` | 反编译核心——`WinUsb_ControlTransfer`/`WinUsb_Initialize` 调用、DDC 帧、bRequest/WValue/WIndex 取值均从此比对得出 |
| `decompiled\Zeasn.Equipment.Entity.Lib\...decompiled.cs` | 输入源枚举（DP1/DP2/HDMI1/HDMI2 → 0x0F~0x12）定义处 |
| `GMeun\resources\bin\Zeasn.USB.ENE.Lib.dll` | **ENE 厂商 USB 库**——AG275UXM 的 USB 主控为 ENE 芯片（`USBViewAll.txt` iManufacturer="ENE"），G-Menu 分派此路径。0.6.6 已反汇编配套原生 `EneEc.dll` 确认其 WinUSB 控制传输：写 0x80 / 读 0x81、reg 地址 0x6E + 寄存器 0x51（本工具本次切源的真正依据） |
| `GMeun\resources\bin\EneEc.dll` | **ENE 原生库**（非 .NET，C++）——`Zeasn.USB.ENE.Lib.dll` 的底层，动态 `LoadLibrary("winusb.dll")` + GetProcAddress 绑定 `WinUsb_ControlTransfer/Initialize/Free`；0.6.6 已用 capstone 反汇编 .text，定位 ENE 私有路径的真实 bRequest（0x80 写 / 0x81 读）与 reg 结构（writer 0x32C0 / reader 0x31F0） |
| `GMeun\resources\bin\Zeasn.DDC.Lib.dll` | DDC/CI 通用库，DDC 帧格式参考 |
| `G-Menu_反汇编总结.md` | 早期反汇编总结笔记 |
| `AocInputSwitcher\协议说明.md` | 协议层详细说明 |
| `USBViewAll.txt` | 目标设备 USB 描述符 dump（确认 MI_00 为 0xFF / 0 端点） |
| `AI开发功能说明.md` | 版本迭代记录（每次编译必更） |

## 版本

当前版本 **0.6.6**。完整版本迭代记录见项目根目录 [`AI开发功能说明.md`](../AI开发功能说明.md)。

> 另见新工具 [`ddc.exe`](../ddc/README.md)：纯 Windows DDC/CI（MCCS/dxva2，经 DP1 的 DDC 信号通路）切换输入源，
> 无需 USB 上行线、厂商 DLL 或 G-Menu，与 AocInputSwitcher 并行可用。
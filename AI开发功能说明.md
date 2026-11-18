# AI 开发功能说明

本文档记录 `d:\Ai\G-Menu` 项目（G-Menu 逆向分析 + AocInputSwitcher 自研工具）的编译迭代记录。
按项目规则：仅允许在本文档末尾增量追加，禁止覆盖原有内容。

---
## 版本迭代记录
### 版本号：0.2.0
- 编译时间：2026-10-14 16:45
- 环境配置：未使用 BAT 环境；编译命令 `C:\Windows\Microsoft.NET\Framework\v4.0.30319\csc.exe /platform:x86 /out:AocInputSwitcher.exe Program.cs`（工作目录 `d:\Ai\G-Menu\AocInputSwitcher`）
- 编译结果：编译成功（csc 4.8.9232.0，无错误无警告）
- 本次版本变更概要：
  1. 新增：
     - `AocInputSwitcher.exe test` 命令：协议离线自检，构建全部 5 个 DDC/CI 帧（查询 + DP1/DP2/HDMI1/HDMI2），重算 XOR 校验并与官方帧逐字节比对；纯 C# 字节运算，不加载 RhHidAPI.dll、无需连接显示器（Program.cs 新增 `RunSelfTest()`）
     - `AocInputSwitcher.exe list` 命令：SetupAPI 枚举本机全部含 VID/PID 的 present 设备节点并高亮目标显示器桥 VID_05F6/PID_AC2F；不加载 RhHidAPI.dll（Program.cs 新增 `RunDeviceList()`）
     - 工作目录根新增 `G-Menu_反汇编总结.md`：REA v6.5.0 + Ghidra 12.1.4 对 G-Menu / RhHidAPI.dll 的完整反汇编记录（导出表、vtable 偏移、DDC 协议、环境复用指南）
  2. 修改：
     - Program.cs `Monitor.BuildFrame` 可见性 `private` → `internal`（供 test 命令复用，逻辑未动）
     - Program.cs `Main` 在 `Monitor.Open()` 前新增 test/list 离线命令分流
     - Program.cs Open 失败提示新增第⑤条（无显示器环境引导 test/list）
     - Program.cs `RunCommand` default 帮助文案补充 test/list
     - `AocInputSwitcher/协议说明.md`：§2 新增"REA + Ghidra 原生反汇编验证"小节、§4 运行命令补充 test/list、§5 排查表新增一行
  3. 删除：无
  4. 兼容说明：完全兼容旧版本。test/list 为新增独立命令，Main 分流在 Open 之前执行；hdmi1/hdmi2/dp1/dp2/query/diag 真机链路代码路径未改动，已回归验证（无显示器环境 hdmi1 仍按原逻辑报 device not found，退出码 2）
- 验证记录：
  - `AocInputSwitcher.exe test`：5 通过 / 0 失败（查询帧 6E 51 82 01 60 DC、HDMI1 帧 6E 51 84 03 60 00 11 C9 等全部与官方一致）
  - `AocInputSwitcher.exe list`：正确报告未找到 VID_05F6/PID_AC2F（开发机无显示器），列出本机 2 个其它 VID 设备节点
  - `AocInputSwitcher.exe hdmi1`：行为与改动前一致，确认真机路径零回归
- 备注：本记录为本文档首条记录。基线 0.1.0 对应此前未记录的"官方 RhHidAPI 通道版"（真机切换链路）。当前开发机无显示器，HDMI/DP 实际切换待连接显示器后真机验证。

---
### 版本号：0.2.1
- 编译时间：2026-10-15 03:00
- 环境配置：未使用 BAT 环境；编译命令 `C:\Windows\Microsoft.NET\Framework\v4.0.30319\csc.exe /platform:x86 /out:AocInputSwitcher.exe Program.cs`（工作目录 `d:\Ai\G-Menu\AocInputSwitcher`）
- 编译结果：编译成功（csc 4.8.9232.0，无错误；首次编译因枚举名 `LPWideStr` 拼写错误报 CS0117，修正为 `LPWStr` 后通过）
- 本次版本变更概要：
  1. 新增：无（`RH_ConfigLog` 为既有导出函数，本版本首次启用，仅用于诊断）
  2. 修改：
     - Program.cs `Native.RH_OpenHid` P/Invoke 签名由 `IntPtr RH_OpenHid(uint dwDevInst)` 改为 `IntPtr RH_OpenHid([MarshalAs(UnmanagedType.LPWStr)] string DevInst)`
     - Program.cs `RhHidChannel.TryOpen` 调用点由 `Native.RH_OpenHid(node.DevInst)`（SetupAPI 枚举索引）改为 `Native.RH_OpenHid(node.DeviceId)`（完整实例 ID 字符串）
     - Program.cs `TryOpen` 的 3 个 return 路径前及开头 verbose 分支增加 `RH_ConfigLog` 启用/关闭（1 启用、3 关闭），捕获 DLL 内部日志
  3. 删除：无
  4. 兼容说明：完全兼容旧版本。帧构建、DDC payload、输入源编码、协议时序均未动；仅通道打开方式由"枚举索引"修正为"实例 ID 字符串"，与 DLL 实际实现一致。真机行为待用户接显示器验证
- 变更根因记录：Ghidra 反汇编证实 `RH_OpenHid` 内部将首参当【设备实例 ID 宽字符串】传给 `CM_Get_Device_IDW`（再从其中解析 "vid_"/"pid_" 按 VID/PID 打开 HID）；旧版传入 SetupAPI 枚举索引（uint 小整数）被当指针解引用 → 访问违例 → SEH 捕获 → 返回 NULL，故真机 4 个候选节点全失败。同时本机官方 `RH_ListAllHidDev` 返回 count=0（官方 RTK 枚举路径在本机已失效），本工具以自枚举 + 字符串参数独立打开
- 验证记录：
  - 编译成功，进程位数确认 32 位
  - `AocInputSwitcher.exe test`：5 通过 / 0 失败（协议帧与官方逐字节一致，零回归）
  - 本环境无显示器（`list` 确认 0 目标节点），真机验证待用户执行：在 `I:\aoc` 运行 `diag`，期望某节点出现 `RH_OpenHid 成功 pDev=0x...`、`Config(...)=0`、`*** 检测到 DDC 应答帧 6E/8F ***`
---
### 版本号：0.3.0
- 编译时间：2026-10-15 13:14
- 环境配置：未使用 BAT 环境；编译命令 `C:\Windows\Microsoft.NET\Framework\v4.0.30319\csc.exe /platform:x86 /out:AocInputSwitcher.exe Program.cs`（工作目录 `d:\Ai\G-Menu\AocInputSwitcher`）
- 编译结果：编译成功（csc 4.8.9232.0，无错误；首次因 `0x80000000` 超出 int 范围报 CS0266、二次因访问级别报 CS0122，均修正后通过）
- 本次版本变更概要：
  1. 新增：
     - `AocInputSwitcher.exe hid` 命令：纯 HID 直连诊断。用系统自带 hid.dll（`HidD_OpenDeviceW`）逐个打开候选节点并打印成败 + Win32 错误码；打开成功的节点用 `HidD_GetPreparedData` 读取完整报告描述符并 hex dump，用于定位 DDC 帧所在 report（report ID/偏移/长度）。不加载 RhHidAPI.dll，任意位数进程均可运行（Program.cs 新增 `RunHidDiag()`；Native 新增 hid.dll P/Invoke 4 项 + 访问标志常量 4 项）
  2. 修改：
     - Program.cs `Main` 在 `Monitor.Open()` 前新增 `hid` 离线命令分流
     - Program.cs `RunCommand` default 帮助文案补充 `hid`
  3. 删除：无
  4. 兼容说明：完全兼容旧版本。`hid` 为新增独立离线命令，Main 分流在 Open 之前执行；test/list 及 hdmi1/hdmi2/dp1/dp2/query/diag 真机链路代码路径未改动，已回归验证（test 5/5；无显示器环境 hid 正确报"未找到设备"退出码 2、不崩溃）
- 验证记录：
  - 编译成功，进程位数确认 32 位
  - `AocInputSwitcher.exe test`：5 通过 / 0 失败（零回归）
  - `AocInputSwitcher.exe hid`（开发机无显示器）：正确报告"未找到任何含 VID_05F6/PID_AC2F 的 present 设备节点"，退出码 2，不崩溃
- 备注：本命令为"纯 HID 直连"路线的前置诊断——若真机某【HID 接口】节点被 hid.dll 成功打开并给出报告描述符，则后续可彻底绕开 RhHidAPI.dll 代理驱动实现直连切换；若全部打开失败，则问题归因到 Realtek 代理驱动/安装层面（需用户侧检查驱动）。
---
### 版本号：0.4.0
- 编译时间：2026-10-15 14:30
- 环境配置：`C:\Windows\Microsoft.NET\Framework\v4.0.30319\csc.exe /out:AocInputSwitcher.exe Program.cs`（工作目录 `d:\Ai\G-Menu\AocInputSwitcher`）
- 编译结果：编译成功（EXIT=0，csc 4.8.9232.0；1 个非致命 warning CS0219 未使用变量，零 error）
- 本次版本变更概要：
  1. 新增：
     - 完整的**纯 HID 直连通道**（`DirectHidChannel` 类，`II2cChannel` 实现）：用 `CreateFileW`（kernel32）打开 HID 符号链接路径 → `HidD_GetPreparsedData` 取 descriptor → `HidD_SetFeature`/`HidD_GetFeature` 收发 DDC/CI 帧（SetFeature 失败自动回退 SetOutputReport）→ `ProbeDdc` 探测 6E/8F 应答帧锁定通道
     - `UsbUtil.EnumerateHidInterfaces`：正确的 HID 接口枚举（`SetupDiEnumDeviceInterfaces` + GUID_DEVINTERFACE_HID + `SetupDiGetDeviceInterfaceDetailW` 拿符号链接路径）
     - 正确的系统 API P/Invoke：`CreateFileW`/`CloseHandle`（kernel32）、`HidD_GetHidGuid`/`HidD_GetPreparsedData`/`HidD_FreePreparsedData`/`HidD_SetFeature`/`HidD_GetFeature`/`HidD_SetOutputReport`/`HidD_GetInputReport`（hid.dll）
     - 新增结构 `SpDeviceInterfaceData`、`SpDeviceInterfaceDetailDataW`、类 `HidInterfaceNode`；硬编码 HID 类 GUID `{4D36E96D-E325-11CE-BFC1-08002BE10318}`
  2. 修改：
     - **彻底移除 RhHidAPI.dll 依赖**：删除 `RhHidChannel` 类、`RhDeviceInfo` 结构、所有 RhHidAPI P/Invoke、`Native` 中 RhHidAPI 常量
     - `Monitor.Open` 切换到 `DirectHidChannel`（唯一通道），移除 x86 强制位数检查（hid.dll 随 OS，x86/x64 均可）
     - `RunHidDiag` 彻底重写：正确走 `CreateFile + SetupDiEnumDeviceInterfaces` 路径（不再用之前错误的 `HidD_OpenDevice` 系列——该系列根本不是系统 hid.dll 的导出函数）
     - `Monitor.RawRead` 缓冲区扩展到 64 字节（原 32 字节，HID Feature Report 常用 64 字节）
     - 标题/提示文案全面更新（去掉 RhHidAPI 依赖说明，改为"纯 HID 直连"）
  3. 删除：RhHidAPI.dll 部署不再需要（exe 零依赖，系统自带 hid/kernel32/setupapi 即可）
  4. 兼容说明：协议层零改动（`BuildFrame`、输入源编码、`SourceName` 全不动），test 命令 5/5 通过证实帧格式零回归。部署简化为单文件 exe（不再附带 RhHidAPI.dll）。
- 变更根因记录：
  - RhHidAPI.dll 内部依赖 "代理驱动"（Init Proxy Driver / CHidTransfers 全局 I/O 引擎）+ "内部查找器"（CHidFind::EnumAllDevs 枚举所有设备但用代理驱动过滤，真机 count=0 → RH_ListAllHidDev count=0；RH_OpenHid 内部 WaitDevConnect 永超时 → 4 节点全 NULL）。**RhHidAPI 通道在当前环境彻底不可用**
  - 错误的 hid.dll P/Invoke：两次写成 `HidD_OpenDeviceW` / `HidD_OpenDevice`，真机均报"找不到入口点"——该系列根本不是系统 hid.dll 的导出函数。正确路径是 `CreateFile`（kernel32）打开符号链接路径 + `HidD_*`（hid.dll）做 I/O（AdvisorTool 诊断确认）
  - USBViewAll.txt 硬件证据：目标设备 VID_05F6/PID_AC2F 是 USB Composite Device，两个接口 MI_00（vendor-specific 0xFF 灯效，EP0-only）和 MI_02（HID 0x03 DDC 通道，1 IN endpoint 0x82 32 字节，Report Descriptor 36 字节）。MI_02 无显式 OUT endpoint → 写必须走 SET_REPORT 控制传输
- 验证记录：
  - **编译成功**（EXIT=0，零 error，进程位数 64 位正常）
  - `test`：**5 通过 / 0 失败**（帧格式零回归，与官方 G-Menu 反编译逐字节一致）
  - `hid`（开发机无显示器）：正确报告"未找到任何含 VID_05F6/PID_AC2F 的 HID 接口"，退出码 2，**不崩溃**
  - `hid --vid 0627 --pid 0001`（开发机现有 HID 设备）：本机 VID_0627 未被枚举为 `GUID_DEVINTERFACE_HID` 接口（该设备可能注册方式不同），但真机目标 MI_02 接口已知是标准 HID 类（USBViewAll.txt 证实 bInterfaceClass=0x03），肯定能被枚举到
  - 真机验证待用户执行：在 `I:\aoc`（接显示器的机器）跑 `hid` → 期望 CreateFile 成功 + GetPreparsedData 成功；随后 `diag`/`query`/`hdmi1` 验证 DDC 通道
---
### 版本号：0.4.1
- 编译时间：2026-10-15 15:30
- 环境配置：`C:\Windows\Microsoft.NET\Framework\v4.0.30319\csc.exe /out:AocInputSwitcher.exe Program.cs`（工作目录 `d:\Ai\G-Menu\AocInputSwitcher`）
- 编译结果：编译成功（EXIT=0，零 error 零 warning）
- 本次版本变更概要：
  1. 新增：无
  2. 修改：
     - Program.cs **修复 HID 接口 GUID 错误**：原硬编码 `{4D36E96D-E325-11CE-BFC1-08002BE10318}` 实为 GUID_DEVCLASS_MODEM（Modem 设备类），导致 SetupDiEnumDeviceInterfaces 对 HID 接口枚举恒返回空集（真机+本地键盘均 0 个）。修正为正确值 `{4D1E55B2-F16F-11CF-88CB-001111000030}`（GUID_DEVINTERFACE_HID），并改为运行时 `HidD_GetHidGuid` 获取（双保险）
     - Program.cs **修复 SetupDiGetDeviceInterfaceDetailW 的 cbSize 错误**：原写入整个 buffer 总长（requiredSize），API 要求写入结构头固定大小（32 位=6、64 位=8）；修正为 `IntPtr.Size == 8 ? 8 : 6`
     - Program.cs **修复 DevicePath 偏移量**：路径紧跟 DWORD cbSize 之后从 offset 4 开始（32/64 位均如此），原误用 cbSize（8）导致路径首字符被截断（`\hid#...` 缺 `\\`）→ CreateFile 报 err=123 ERROR_INVALID_NAME
     - Program.cs 删除 `DirectHidChannel.TryOpen` 中重复的 `HidD_GetPreparsedData` 调用（内存泄漏）
  3. 删除：无
  4. 兼容说明：协议层零改动，test 5/5 零回归
- 验证记录：
  - **本地键盘设备（VID_0627/PID_0001）决定性验证**：GUID 修正后成功枚举到 HID 接口（符号链接 `\\?\hid#vid_0627&pid_0001#...#{4d1e55b2-...}` 完整正确）→ CreateFile 正确调用并返回 err=5（ACCESS_DENIED，键盘被系统独占属预期行为，证明调用链完全正确）
  - `test`：5 通过 / 0 失败（零回归）
  - `hid`（开发机无显示器）：正确报"未找到"，退出码 2，不崩溃
  - 真机验证待用户执行：AOC MI_02 是厂商私有 DDC 通道（非键鼠类，不被独占），期望 `CreateFile 成功` + `HidD_GetPreparsedData 成功`
---
### 版本号：0.4.2
- 编译时间：2026-10-15 16:00
- 环境配置：`C:\Windows\Microsoft.NET\Framework\v4.0.30319\csc.exe /out:AocInputSwitcher.exe Program.cs`
- 编译结果：编译成功（EXIT=0，零 error）
- 本次版本变更概要：
  1. 新增：`hid` 命令升级为**深度诊断**——多模式试开（完整RW/只读/零访问/独占/设备路径去GUID）+ UTF-16 hex dump + 对照组（其它 HID 设备）验证机制 + Win32 错误码语义解读
  2. 修改：`RunHidDiag` 全量枚举 HID 接口（不过滤），分组目标/对照；`UsbUtil.EnumerateHidInterfaces` 支持空 vid/pid 全量枚举
  3. 删除：无
  4. 兼容说明：协议层零改动，test 5/5 零回归
- 本地验证关键发现（键盘 VID_0627）：
  - **零访问模式（0+共享RW）成功打开**被系统独占的键盘 → 真机 AOC 若被占用可试此模式
  - **设备路径去 #{GUID} 报 err=2** → 证实符号链接必须包含 `#{GUID}` 尾部（真机 AOC 的 err=2 不是格式问题）
  - 对照组（3 个腾讯 HID）全部成功打开 → 枚举+CreateFile 机制通用可用
  - 全量枚举到 8 个 HID 接口（键盘 1 + 腾讯 7）→ 枚举机制正确
- 真机待验证：AOC MI_02 多模式试开，期望零访问或其它模式成功
---
### 版本号：0.5.0
- 编译时间：2026-10-15 18:30
- 环境配置：`C:\Windows\Microsoft.NET\Framework\v4.0.30319\csc.exe /out:AocInputSwitcher.exe Program.cs`
- 编译结果：编译成功（EXIT=0，零 error）
- 重大方向修正：用户确认 G-Menu 走的是 **rawusb 私有协议（vendor 0xFF 接口）**，非 HID。重新逆向 `Zeasn.Monitor.Lib.decompiled.cs`（2.4MB）确认：G-Menu 用 **WinUSB 控制传输**（`WinUsb_Initialize`/`WinUsb_ControlTransfer`）向 MI_00 vendor 接口发 I2C 报文承载 DDC/CI 帧。SetupPacket = 标准 WINUSB_SETUP_PACKET（bmRequestType/bRequest/wValue/wIndex/wLength）；写=0x40+0xA8/0xA9、读=0xC0+0xA3（混淆代码推断，真机自动遍历）；重试 3 次、超时 177ms。输入源编码 `StandardSource_E` 证实与 BuildFrame 一致（DP1=0x0F DP2=0x10 HDMI1=0x11 HDMI2=0x12）——**协议层零改动**。
- 本次版本变更概要：
  1. 新增：
     - `WinUsbRawChannel`（II2cChannel 实现，rawusb 私有协议通道）：GUID_DEVINTERFACE_USB_DEVICE 枚举 → CreateFile → WinUsb_Initialize → WinUsb_ControlTransfer（vendor 0x40/0xC0 + wValue=0x51）收发 DDC 帧；ProbeDdc 自动遍历写 bRequest（0xA8/0xA9/0xA7/0xB8/0xB9）× 读 bRequest（0xA3/0xB7/0xA8）候选组合，锁定能拿到 6E 8F 应答的通道
     - `UsbUtil.EnumerateUsbInterfaces`（USB 设备接口枚举）
     - winusb.dll P/Invoke（WinUsb_Initialize/Free/ControlTransfer）+ `WinUsbSetupPacket` 结构 + `GUID_DEVINTERFACE_USB_DEVICE`
     - `AocInputSwitcher.exe rawusb` 诊断命令（RunRawUsbDiag）：逐接口 WinUSB 打开 + 全候选 bRequest 探测 + hex 输出
  2. 修改：`Monitor.Open` 改为双通道顺序打开（WinUSB rawusb 优先、纯 HID 直连后备）
  3. 删除：无
  4. 兼容说明：协议层零改动（BuildFrame/SourceName/test 全不动），test 5/5 零回归；winusb.dll 系统自带，零新依赖
- 验证记录：
  - 编译成功（EXIT=0）
  - `test`：5 通过 / 0 失败（零回归）
  - `rawusb`（开发机无显示器）：正确报"未找到"，退出码 2，不崩溃
  - 真机待验证：用户执行 `rawusb` → 期望 WinUsb_Initialize 成功 + 某候选 bRequest 组合命中 DDC 应答 6E/8F；随后 `hdmi1`/`query` 切换验证
---
### 版本号：0.6.0
- 编译时间：2026-10-15 19:30
- 环境配置：`C:\Windows\Microsoft.NET\Framework\v4.0.30319\csc.exe /out:AocInputSwitcher.exe Program.cs`
- 编译结果：编译成功（EXIT=0，零 error 零 warning）
- 重大根因定位（真机 PowerShell 确认）：MI_00 = `USBDevice` 类 / **Service=WINUSB** / winusb.inf——WinUSB 驱动已正确绑定！此前 `WinUsb_Initialize err=6`（INVALID_HANDLE）根因：按标准 GUID_DEVINTERFACE_USB_DEVICE 枚举只抓到 usbccgp **父设备**（`usb#vid_05f6&pid_ac2f#9876543210` 无 `&mi_00`），WinUSB 不能初始化父设备。MI_00 接口注册的是 winusb.inf **自定义 DeviceInterfaceGUID**（G-Menu 反混淆 GUID `yeThMRBbeb`），须全接口扫描才能抓到其接口级符号链接。
- 本次版本变更概要（用户指令：只保留纯 USB，移除全部 HID 功能）：
  1. 新增：`UsbUtil.EnumerateUsbInterfaces` 改为**全设备接口扫描**（`DIGCF_ALLCLASSES|DIGCF_DEVICEINTERFACE|DIGCF_PRESENT`，不按 GUID 过滤）+ VID/PID 过滤 + MI_00 优先排序——保证抓到 MI_00 接口级符号链接（形如 `\\?\usb#vid_05f6&pid_ac2f&mi_00#...#{自定义GUID}`）
  2. 修改：`Monitor.Open` 唯一通道为 `WinUsbRawChannel`（rawusb）；rawusb 诊断输出标注"父设备 err=6 属预期"
  3. 删除：**全部 HID 功能**——`DirectHidChannel`、`RunHidDiag`、hid.dll P/Invoke（HidD_*）、HID 接口枚举（`EnumerateHidInterfaces`/`HidInterfaceNode`）、`hid` 命令、GUID_DEVINTERFACE_HID
  4. 兼容说明：协议层零改动（BuildFrame/SourceName/test 不动），test 5/5 零回归；winusb.dll 系统自带零依赖
- 验证记录：
  - 编译成功（EXIT=0）
  - `test`：5 通过 / 0 失败（零回归）
  - `rawusb`/`list`（开发机无显示器）：正确报"未找到"，退出码 2/1，不崩溃
  - 真机待验证：`rawusb` 期望全接口扫描抓到 `&mi_00` 路径 → WinUsb_Initialize 成功 → 某 bRequest 候选命中 6E 8F 应答 → `hdmi1` 切换
---
### 版本号：0.6.1
- 编译时间：2026-10-15 20:15
- 环境配置：`C:\Windows\Microsoft.NET\Framework\v4.0.30319\csc.exe /out:AocInputSwitcher.exe Program.cs`
- 编译结果：编译成功（EXIT=0；首次报 CS1657 foreach 变量不能作 ref 传参，改局部拷贝后通过）
- 变更根因：0.6.0 的"全接口扫描"（SetupDiEnumDeviceInterfaces 传 GUID_NULL）在真机枚举恒空——该 API 的 InterfaceClassGuid 参数是过滤器，传空 GUID 匹配不到任何接口类。正确做法：读注册表 `HKLM\SYSTEM\CurrentControlSet\Enum\{实例ID}\Device Parameters\DeviceInterfaceGUID(s)` 拿 winusb.inf 写入的自定义接口 GUID，再按该 GUID 枚举/构造符号链接。
- 本次版本变更概要：
  1. 新增：`UsbUtil.ReadDeviceInterfaceGuids`（读注册表 DeviceInterfaceGUIDs/DeviceInterfaceGUID）；`EnumerateUsbInterfaces` 重写为"设备节点→注册表 GUID→双路取符号链接"（路径 A：SetupDi 按 GUID 枚举；路径 B：直接构造 `\\?\{实例ID变形}#{GUID}`）；`using Microsoft.Win32`
  2. 修改：rawusb 诊断输出新增"注册表接口 GUID"诊断行
  3. 删除：无（WinUsbRawChannel/Monitor/BuildFrame/test 全不动）
  4. 兼容说明：协议层零改动，test 5/5 零回归
- 验证记录：编译成功；test 5/5；rawusb（开发机无显示器）正确报"未找到"不崩溃。真机待验证：rawusb 期望输出注册表 GUID 行 → `&mi_00` 符号链接 → WinUsb_Initialize 成功（不再 err=6）→ bRequest 候选命中 6E 8F → hdmi1 切换
---
### 版本号：0.6.2
- 编译时间：2026-10-15 19:54
- 环境配置：`C:\Windows\Microsoft.NET\Framework\v4.0.30319\csc.exe /out:AocInputSwitcher.exe Program.cs`
- 编译结果：编译成功（EXIT=0，零 error 零 warning）
- 变更根因：真机 0.6.1 结果——路径 A（SetupDi 按注册表 GUID 枚举）已拿到正确 MI_00 符号链接（`\\?\usb#...#{6987b675-...}`，CreateFile 成功 h=0x304），但 **WinUsb_Initialize 仍 err=6（INVALID_HANDLE）**。对已成功打开的句柄返回 err=6 属于设备栈层面问题（接口背后功能驱动非 WinUSB / 驱动未启动 / 被其他进程占用），路径层面已无问题。本版转向「设备状态 + 驱动绑定 + 独占探测」诊断；顺带发现并修复路径 B 构造缺花括号 bug（致 err=2 干扰行）。
- 本次版本变更概要：
  1. 新增：`Native.CM_Get_DevNode_Status`（cfgmgr32.dll P/Invoke，只读）+ `DN_STARTED`/`DN_HAS_PROBLEM` 常量；`UsbUtil.NodeDiagnosis`（读注册表 Service/ClassGUID + Control\Class 类友好名 + 启动状态/问题码）+ `ProblemName` 问题码说明；rawusb 诊断新增「节点诊断」行与 FILE_SHARE_NONE 独占探测（err=32 判定被 G-Menu 等进程占用）
  2. 修改：修复路径 B 花括号缺失 bug（`#{GUID}` 补 `{}`，缺失致路径无效 err=2）；路径 B 改为仅路径 A 无结果时兜底（消除重复干扰行）；WinUsb_Initialize 错误码先存变量再输出（避免独占探测覆盖 last error）
  3. 删除：无（WinUsbRawChannel/Monitor/BuildFrame/test 全不动）
  4. 兼容说明：协议层零改动，test 5/5 零回归；新增全部为只读查询，无设备状态副作用；无新依赖（cfgmgr32.dll 系统自带）
- 验证记录：编译成功；test 5/5；rawusb/list（开发机无显示器）正确报"未找到"（退出码 2/1）不崩溃。真机待验证：rawusb 输出各节点 Service/Class/状态 → MI_00 若 Service≠WinUsb 或问题码非 0 则需重装/修复驱动；若独占探测 err=32 则彻底退出 G-Menu；若一切正常仍 err=6 则用 Process Monitor 抓 G-Menu 实际设备访问序列
---
### 版本号：0.6.3
- 编译时间：2026-10-15 21:52
- 环境配置：`C:\Windows\Microsoft.NET\Framework\v4.0.30319\csc.exe /out:AocInputSwitcher.exe Program.cs`
- 编译结果：编译成功（EXIT=0，零 error 零 warning）
- 变更根因：0.6.2 真机结果——MI_00 的 Service=WINUSB、无 filter（Upper/LowerFilters 空）、G-Menu 已完全退出、FILE_SHARE_NONE 独占探测通过（无文件级占用），WinUsb_Initialize 仍 err=6。逐参比对 G-Menu 反编译 OpenDevice→CreateFile(tUchwm67Zm(...,3,3,0x40000000,...))，唯一差异为本工具 CreateFile 的 dwFlagsAndAttributes=0。本版改为 G-Menu 原值 0x40000000。
- 本次版本变更概要：
  1. 新增：`Native.CREATEFILE_FLAGS_WINUSB` 常量（0x40000000，取自 G-Menu 反编译 tUchwm67Zm 调用原值）
  2. 修改：`WinUsbRawChannel.TryOpen` 与 `RunRawUsbDiag` 的 `CreateFile`，第 6 参 dwFlagsAndAttributes 由 0 改为 `Native.CREATEFILE_FLAGS_WINUSB`
  3. 删除：无
  4. 兼容说明：协议层零改动（BuildFrame/输入源编码/SourceName 全不动），test 5/5 零回归；仅改变 WinUSB 通道 CreateFile 打开方式，对 HID 通道、枚举、帧构建无影响；winusb.dll 系统自带零新依赖
- 验证记录：编译成功（EXIT=0）；test 5 通过 / 0 失败（零回归）。真机待验证：`rawusb` 期望 MI_00 符号链接行 → WinUsb_Initialize 成功（usb≠0、不再 err=6）→ bRequest 候选命中 6E 8F → `hdmi1/dp1/query` 可切源；若仍 err=6 则 flag 非根因，转 Process Monitor 定位
---
### 版本号：0.6.4
- 编译时间：2026-10-15 22:22
- 环境配置：`C:\Windows\Microsoft.NET\Framework\v4.0.30319\csc.exe /out:AocInputSwitcher.exe Program.cs`
- 编译结果：编译成功（EXIT=0，零 error 零 warning）
- 变更根因：0.6.3 真机 rawusb 已证 WinUsb_Initialize 成功，但控制传输全候选 err=31（INVALID_PARAMETER）。反编译 G-Menu 全量取值：DDC 传输 wValue 恒 0（全代码 VWuvP7x9xP 仅被赋 0），我误用 WValue=0x51 致驱动拒参；且 bRequest 候选漏 G-Menu 的 0xB2/0xB7。本版将 wValue 改 0 并按 G-Menu 全量扩充 bRequest。
- 本次版本变更概要：
  1. 新增：无（仅改既有结构/数组/注释）
  2. 修改：`ControlTransfer` 的 `WValue=0x51`→`0`（覆盖所有真实读写 + 探测）；`WinUsbRawChannel.WRITE_REQUESTS/READ_REQUESTS` 扩至 G-Menu 全量；`RunRawUsbDiag` 两处 setupOut/setupIn `WValue=0x51`→`0` + bRequest 内联数组扩充；顶部协议注释 wValue 同步为 0
  3. 删除：无
  4. 兼容说明：协议帧（BuildFrame/输入源编码/SourceName）零改动，test 5/5 零回归；仅改 ControlTransfer 的 wValue 与 bRequest 候选集，对枚举/HID 无影响
- 验证记录：编译成功（EXIT=0）；test 5 通过 / 0 失败（零回归）。真机待验证：`rawusb` 期望某 bRequest 组合 WinUsb_ControlTransfer 成功 + 命中 6E 8F → 锁定通道 → `hdmi1/dp1/query` 切源
---
### 版本号：0.6.5
- 编译时间：2026-10-16 00:42
- 环境配置：`C:\Windows\Microsoft.NET\Framework\v4.0.30319\csc.exe /out:AocInputSwitcher.exe Program.cs`
- 编译结果：编译成功（EXIT=0，零 error 零 warning）
- 变更根因：0.6.4 真机 rawusb——WinUsb_Initialize 成功，但 OUT 控制传输全候选仍 err=31。反编译 G-Menu 各 builder 精确定位到 `neTvRJKo3J = P_0[0]`（即 WIndex=载荷首字节）全部出现在 IN/读方向（bmRequestType=0xC0）的 builder 中，说明「查询输入源 Get VCP」实际是独立 IN 控制传输（wIndex=命令首字节 0x82），而非本工具此前只做的「先写 OUT 再读 IN」序列；rawusb 每次 OUT 失败即 continue，从未走到 IN。本版在 rawusb 增加独立 IN 扫描。
- 本次版本变更概要：
  1. 新增：`RunRawUsbDiag` 增加「独立 IN 扫描」——不依赖 OUT，逐一读 bRequest{0xA3,0xB7,0xA9,0xA8,0xA7,0xB2} × wIndex{0x0082,0x0000}，命中 DDC 应答 6E 8F 即锁定并输出 readReq/wIdx
  2. 修改：无（协议帧 BuildFrame、ControlTransfer、WRITE/READ_REQUESTS、WValue、CreateFile flag 全不动）
  3. 删除：无
  4. 兼容说明：纯诊断新增，协议层零改动，test 5/5 零回归；对真实命令（hdmi1/dp1/query）无副作用
- 验证记录：编译成功（EXIT=0）；test 5 通过 / 0 失败（零回归）。真机待验证：`rawusb` 独立 IN 扫描某 readReq+wIdx 组合返回成功并命中 6E 8F → 据此锁定 IN 传输参数并对齐真实 ControlTransfer
---
### 版本号：0.6.6
- 编译时间：2026-10-16 01:51
- 环境配置：`C:\Windows\Microsoft.NET\Framework\v4.0.30319\csc.exe /out:AocInputSwitcher.exe Program.cs`
- 编译结果：编译成功（EXIT=0，零 error 零 warning）
- 变更根因：0.6.5 真机 rawusb——WinUsb_Initialize 成功，但独立 IN 扫描 + OUT 扫描全候选仍 err=31。本轮反汇编 ENE 厂商原生库 `EneEc.dll`（capstone 全量反汇编 .text）定位到 ENE 私有路径的真实 WinUSB 控制传输：写 bmRequestType=0x40 bRequest=0x80、读 bmRequestType=0xC0 bRequest=0x81（反汇编 writer 0x32C0 / reader 0x31F0 确认），且 reg 32 位拆为 wValue（高16=地址 0x6E）/wIndex（低16=寄存器 0x51）。此前从通用路径 Zeasn.Monitor.Lib 逆出的 bRequest（0xA3/0xB7/0xA9/0xA8/0xA7/0xB2）对 ENE 芯片无效，设备对每次控制传输 STALL → err=31。本机 AG275UXM USB 主控为 ENE 芯片（USBViewAll.txt iManufacturer="ENE"），G-Menu 分派 ENE 私有路径（EneEc.dll），故须用 ENE 真实值。
- 本次版本变更概要：
  1. 新增：`REG_CANDIDATES`（7 个 reg 32 位候选，首选 0x6E0051）+ `_reg` 字段；rawusb 诊断新增「ENE 路径」探测块（写 req=0x80 reg=0x6E0051、读 req=0x81 reg=0x6E0051，命中 6E/8F 即锁定）
  2. 修改：`WRITE_REQUESTS`/`READ_REQUESTS` 首项加 0x80/0x81（旧通用候选保留兜底）；`ControlTransfer` 加 reg 参数（WValue=reg>>16、WIndex=reg&0xFFFF，替代恒 0）；`ProbeDdc` 新增 reg 遍历维度；`RawWrite`/`RawRead`/`Describe` 用锁定 `_reg`；顶部协议注释同步为 ENE 路径
  3. 删除：无
  4. 兼容说明：协议帧（BuildFrame/输入源编码/SourceName）零改动，test 5/5 零回归；仅改 WinUSB 控制传输的 bRequest 与 reg，枚举/HID/test 无影响
- 验证记录：编译成功（EXIT=0）；test 5 通过 / 0 失败（零回归）。真机待验证：`rawusb` 的「ENE 路径」块 OUT req=0x80 reg=0x6E0051 返回成功 + 读 req=0x81 命中 6E/8F → 锁定通道 → `hdmi1/dp1/query` 可切源；若 reg 编码非 0x6E0051，rawusb ENE 块失败但会输出 err/xfer，据此反馈调整 REG_CANDIDATES 候选顺序
---
### 版本号：0.7.0
- 编译时间：2026-10-16 18:45
- 环境配置：未使用 BAT 环境；编译命令 `C:\Windows\Microsoft.NET\Framework\v4.0.30319\csc.exe /nologo /platform:x86 /out:AocInputSwitcher.exe Program.cs`（工作目录 `d:\Ai\G-Menu\AocInputSwitcher`，x86，RhHidAPI.dll 32 位原生库须同目录）
- 编译结果：编译成功（csc，EXIT=0，零 error 零 warning）
- 变更根因：G-Menu .NET 反编译（Zeasn.Monitor.Lib 的 RTKRTKApi 类）证实 `RH_OpenHid` 入参是 RhHidAPI 内部设备索引（uint，由 `RH_ListAllHidDev` 枚举得到的 `Device_Info.dwInst`），0.6.6 及之前版本传入 SetupAPI 设备实例 ID 字符串会被 DLL 当作指针截断解引用 → 真机连显示器时 4 个候选节点 RH_OpenHid 全返回 NULL。同时确认 MI_02=HID 类/Realtek USB‑I2C 桥才是 G-Menu 切换信号源的实际通道（MI_00 为 SPI Flash Writer，非切源通道），故整条通道从 WinUSB rawusb 私有路径改为复刻 G-Menu 的官方 RhHidAPI 枚举流程
- 本次版本变更概要：
  1. 新增：
     - `HidRawChannel` 通道类（MI_02 Realtek USB‑I2C 桥）：`OpenByRtK` 复刻 G-Menu（RTKRTKApi）打开流程——RH_ConfigLog(true,0) → RH_ListAllHidDev → Device_Info[] 解析 → VID/PID 过滤 + ScoreHid(MI_02 优先排序) → RH_OpenHid(dwInst) → RH_HidI2CConfig(0x6E,1,0) → ProbeDdc，失败时 RH_FreeDevList 释放设备表
     - `Native.RH_ListAllHidDev`/`RH_FreeDevList`/`RH_ConfigLog` P/Invoke（签名与 G-Menu 反编译一致）
     - `Device_Info` 结构体（dwInst/hwID[1024]/Path[1024]/PID[5]/VID[5]/nLevel/nPort/DevType/UsbSpeed，逐字段对齐 G-Menu）+ `DEV_TYPE`/`DEV_SPEED` 枚举
     - `UsbUtil.ScoreHid`（节点优先级评分：HID&MI_02 最优先，父设备最后）
  2. 修改：
     - `Native.RH_OpenHid` 签名由 `IntPtr RH_OpenHid(string DevInst)` 改为 `IntPtr RH_OpenHid(uint dwDevInst)`
     - `HidRawChannel.TryOpen` 重写：废弃 SetupAPI 实例 ID 字符串打开，改调 `OpenByRtK`；verbose 日志输出 dwInst/hwID/DevType 便于真机反馈
     - `Monitor.Open`：失败提示文案更新（新增「G-Menu 会独占 MI_02 通道，运行前请完全退出 G-Menu（含后台托盘进程）」）
     - Main：标题与设备行更新为 RhHidAPI MI_02 通道；`RunCommand` 帮助文案同步
     - 文件头与 HidRawChannel 类头注释：通信链更新为「RH_ConfigLog(true,0) → RH_ListAllHidDev → RH_OpenHid(dwInst) → RH_HidI2CConfig → RH_HidI2CWrite/Read」
  3. 删除：
     - `WinUsbRawChannel` 全类（REG_CANDIDATES、WRITE_REQUESTS/READ_REQUESTS、ControlTransfer、ProbeDdc、RunRawUsbDiag、rawusb 命令分流）
     - WinUSB 相关 P/Invoke 与结构体（CreateFile winusb 标志、WINUSB_SETUP_PACKET、WINUSB_INTERFACE_INFO、GUID_DEVINTERFACE_USB_DEVICE 等）
     - `UsbUtil.EnumerateUsbInterfaces`/`ReadDeviceInterfaceGuids`/`NodeDiagnosis`/`ProblemName` 等 rawusb 辅助方法
  4. 兼容说明：DDC/CI 协议帧（BuildFrame）、输入源编码（DP1=0x0F/DP2=0x10/HDMI1=0x11/HDMI2=0x12）、SourceName、ProbeDdc 探测逻辑（Get VCP 0x60 + 6E/8F 扫描）、test 离线自检零改动；`list`（SetupAPI 枚举）、`test`、`ScoreHid` 保留可用。rawusb 诊断命令下线，真机诊断以新 verbose diag 输出为准
- 验证记录：
  - 开发机编译成功（EXIT=0，x86）；进程位数确认 32 位
  - `test`：5 通过 / 0 失败（协议帧与官方逐字节一致，零回归）
  - `list`：开发机无显示器，正确报告未找到 VID_05F6/PID_AC2F，行为与旧版一致
  - `diag`（开发机无显示器）：加载 RhHidAPI.dll 正常，`RH_ListAllHidDev` 返回 0 个设备（本机无 RTK 显示桥 HID 设备，预期），给出明确降级报错「RH_ListAllHidDev 未枚举到任何 HID 设备（返回码=0，数量=0）」，无崩溃
  - 真机待验证（须在接显示器的机器、完全退出 G-Menu 后运行）：`diag`/`hdmi1`/`query`，期望某节点 `RH_OpenHid 成功 pDev=0x...`、`RH_HidI2CConfig 成功`、`*** 检测到 DDC 应答帧 6E/8F ***`、`>>> 锁定 USB‑HID DDC 通道（MI_02）`，且 hdmi1/hdmi2/dp1/dp2 可实际切源、query 能读回当前输入源
---

## 最新版本对比变更概要
### 对比版本：0.7.0 VS 0.6.6
- 对比生成时间：2026-10-16 18:45
- 新增内容：`HidRawChannel` 通道类（MI_02 Realtek USB‑I2C 桥，含 `OpenByRtK`：RH_ConfigLog(true,0) → RH_ListAllHidDev → Device_Info[] 解析 → VID/PID 过滤 + ScoreHid(MI_02 优先) → RH_OpenHid(dwInst) → RH_HidI2CConfig(0x6E,1,0) → 探测 DDC）；`Device_Info` 结构体 + `DEV_TYPE`/`DEV_SPEED` 枚举（逐字段对齐 G-Menu RTKRTKApi）；`Native.RH_ListAllHidDev`/`RH_FreeDevList`/`RH_ConfigLog` P/Invoke
- 修改内容：`Native.RH_OpenHid` 签名由 string 实例 ID 改为 uint 内部索引（G-Menu 反编译确认的根因修复）；`HidRawChannel.TryOpen` 重写为调用 `OpenByRtK`（废弃 SetupAPI 实例 ID 字符串打开）；`Monitor.Open` 失败提示更新（G-Menu 独占 MI_02 通道提示）；Main 标题/设备行/帮助文案同步；文件头与类头通信链注释同步
- 删除内容：`WinUsbRawChannel` 全类 + `rawusb` 诊断命令；WinUSB 相关 P/Invoke/结构体（winusb 标志、WINUSB_SETUP_PACKET 等）；`UsbUtil.EnumerateUsbInterfaces`/`ReadDeviceInterfaceGuids`/`NodeDiagnosis`/`ProblemName` 辅助方法
- 核心影响说明：**参数类型级根因修复**——G-Menu RTKRTKApi 反编译证实 `RH_OpenHid` 入参是 RhHidAPI 内部设备索引（`RH_ListAllHidDev` 枚举 `Device_Info.dwInst`，uint），此前传 SetupAPI 实例 ID 字符串 → 真机 4 节点全 NULL。本版整通道复刻 G-Menu 官方枚举流程。DDC/CI 协议帧、输入源编码、test/list 零改动，test 5/5 零回归；rawusb 诊断命令下线。真机（接显示器 + 完全退出 G-Menu）运行 `diag`/`hdmi1`/`query` 待验证
### 对比版本：0.6.6 VS 0.6.5
- 对比生成时间：2026-10-16 01:51
- 新增内容：`REG_CANDIDATES` 候选数组 + `_reg` 字段；rawusb 新增「ENE 路径」探测块（写 0x80/读 0x81，reg=0x6E0051）
- 修改内容：`WRITE_REQUESTS`/`READ_REQUESTS` 首项 0x80/0x81；`ControlTransfer` reg 参数化（WValue=地址 0x6E / WIndex=寄存器 0x51，替代恒 0）；`ProbeDdc` 加 reg 遍历维度；`RawWrite`/`RawRead`/`Describe` 用 `_reg`；顶部协议注释改 ENE 路径
- 删除内容：无
- 核心影响说明：**根因级修正**——反汇编 `EneEc.dll`（ENE 厂商原生库）确认本机 ENE 芯片显示器走 ENE 私有路径：WinUSB 控制传输 bRequest 写 0x80 / 读 0x81（非通用路径的 0xA3/0xB7/...），reg 32 位拆 wValue(地址 0x6E)/wIndex(寄存器 0x51)。此前用通用路径 bRequest 致设备 STALL（err=31）。本版改 ENE 真实 bRequest + reg 编码，协议帧零改动，test 5/5 零回归。真机 `rawusb` ENE 路径块期望成功并命中 6E/8F。
### 对比版本：0.6.5 VS 0.6.4
- 对比生成时间：2026-10-16 00:42
- 新增内容：`RunRawUsbDiag` 独立 IN 扫描——不依赖 OUT，读 bRequest{0xA3,0xB7,0xA9,0xA8,0xA7,0xB2} × wIndex{0x0082,0x0000}，命中 DDC 应答 6E 8F 即锁定并输出 readReq/wIdx
- 修改内容：无（协议帧 BuildFrame、ControlTransfer、WRITE_REQUESTS/READ_REQUESTS、WValue、CreateFile flag 全不动）
- 删除内容：无
- 核心影响说明：反编译定位 G-Menu 的 `WIndex=载荷首字节` 全部出现在 IN/读方向（bmRequestType=0xC0）builder，判定「查询输入源 Get VCP」为独立 IN 控制传输（wIndex=命令首字节 0x82）；rawusb 此前 OUT 失败即 continue、从未走到 IN。本版补独立 IN 扫描，纯诊断新增、协议层零改动，test 5/5 零回归。
---
### 对比版本：0.6.4 VS 0.6.3
- 对比生成时间：2026-10-15 22:22
- 新增内容：无
- 修改内容：`ControlTransfer` WValue 0x51→0；`WinUsbRawChannel.WRITE_REQUESTS/READ_REQUESTS` 扩至 G-Menu 全量（含 0xB2/0xB7）；`RunRawUsbDiag` 两处 WValue 0x51→0 + bRequest 内联数组扩充；顶部协议注释同步
- 删除内容：无
- 核心影响说明：0.6.3 已通 WinUsb_Initialize，本版修控制传输层——反编译确认 G-Menu DDC 传输 wValue 恒 0、bRequest 全量含 0xB2/0xB7，修正 wValue 与候选集后，期望真机 rawusb 命中 6E 8F 锁定通道。协议帧零改动，test 5/5 零回归。
---
### 对比版本：0.6.3 VS 0.6.2
- 对比生成时间：2026-10-15 21:52
- 新增内容：`Native.CREATEFILE_FLAGS_WINUSB` 常量（0x40000000，G-Menu 反编译原值）
- 修改内容：`WinUsbRawChannel.TryOpen` / `RunRawUsbDiag` 的 `CreateFile` 第 6 参 dwFlagsAndAttributes 由 0 改为 `Native.CREATEFILE_FLAGS_WINUSB`（逐参对齐 G-Menu tUchwm67Zm 原值）
- 删除内容：无
- 核心影响说明：基于 G-Menu 反编译逐参比对定位 err=6——本工具 CreateFile 其余 access/共享/创建/路径与 G-Menu 一致，唯一差异为 dwFlagsAndAttributes（本工具 0 vs G-Menu 0x40000000）。本版对齐该 flag，使 WinUSB 接口可被 WinUsb_Initialize 识别。协议层零改动，test 5/5 零回归。真机 `rawusb` 终验待执行（期望 WinUsb_Initialize 成功）；若仍失败则该 flag 非根因，转 Process Monitor。
---
### 对比版本：0.6.2 VS 0.6.1
- 对比生成时间：2026-10-15 19:54
- 新增内容：`CM_Get_DevNode_Status` P/Invoke + DN_STARTED/DN_HAS_PROBLEM 常量；`UsbUtil.NodeDiagnosis`/`ProblemName`（Service/Class/启动状态/问题码诊断）；rawusb 节点诊断输出 + FILE_SHARE_NONE 独占探测
- 修改内容：修复路径 B 花括号缺失 bug 并改为仅路径 A 无结果时兜底；WinUsb_Initialize 错误码读取顺序修正
- 删除内容：无
- 核心影响说明：真机证实路径已正确（CreateFile h=0x304）而 WinUsb_Initialize 持续 err=6，属设备栈/驱动层面问题。本版将 rawusb 从路径枚举升级为驱动状态诊断（Service/Class/DN_STARTED/问题码）+ 独占判定，按诊断结果定向处置（驱动不对→修驱动；被占用→退 G-Menu）。协议层零改动，test 5/5 零回归。
---
### 对比版本：0.6.1 VS 0.6.0
- 对比生成时间：2026-10-15 20:15
- 新增内容：`UsbUtil.ReadDeviceInterfaceGuids`（注册表读 winusb.inf 自定义 DeviceInterfaceGUID）；EnumerateUsbInterfaces 双路符号链接（SetupDi 按 GUID 枚举 + 实例 ID 变形构造兜底）；rawusb 诊断新增注册表 GUID 输出
- 修改内容：EnumerateUsbInterfaces 重写（修复 GUID_NULL 过滤导致枚举恒空的缺陷）
- 删除内容：无
- 核心影响说明：修复 0.6.0 全接口扫描枚举为 0 的缺陷（SetupDiEnumDeviceInterfaces 的 GUID 参数是过滤器，传空 GUID 匹配不到接口类）。改为读注册表拿 MI_00 自定义接口 GUID 后枚举/构造 `&mi_00` 符号链接，WinUsb_Initialize 应能成功初始化（此前 err=6 系打开父设备）。协议层零改动，test 5/5 零回归。
---
### 对比版本：0.6.0 VS 0.5.0
- 对比生成时间：2026-10-15 19:30
- 新增内容：`EnumerateUsbInterfaces` 升级为全设备接口扫描（不按 GUID 过滤，抓 MI_00 自定义 DeviceInterfaceGUID 下的接口级符号链接）
- 修改内容：`Monitor.Open` 唯一通道改为 WinUsbRawChannel；rawusb 诊断标注父设备 err=6 属预期
- 删除内容：全部 HID 功能（DirectHidChannel、RunHidDiag、hid.dll P/Invoke、HID 接口枚举、hid 命令）
- 核心影响说明：**根因定位 + 纯净化**——真机确认 MI_00=WinUSB(winusb.inf) 驱动已绑定，err=6 系打开 usbccgp 父设备所致（MI_00 注册自定义 GUID，标准 GUID 枚举漏掉它）。全接口扫描保证抓到 `&mi_00` 接口路径。代码纯 USB 化（无 HID 依赖），协议层零改动，test 5/5 零回归。真机 `rawusb` 验证待执行。
---
### 对比版本：0.5.0 VS 0.4.2
- 对比生成时间：2026-10-15 18:30
- 新增内容：`WinUsbRawChannel` rawusb 私有协议通道（WinUSB 控制传输，G-Menu 实际通道）；`UsbUtil.EnumerateUsbInterfaces`；winusb.dll P/Invoke + WINUSB_SETUP_PACKET 结构 + GUID_DEVINTERFACE_USB_DEVICE；`rawusb` 诊断命令（RunRawUsbDiag，bRequest 候选自动遍历）
- 修改内容：`Monitor.Open` 改为双通道顺序打开（WinUSB rawusb 优先、纯 HID 直连后备）；帮助文案补充 rawusb
- 删除内容：无
- 核心影响说明：**方向级修正**——逆向证实 G-Menu 切信号源走 vendor 0xFF 接口的 WinUSB 控制传输（非 HID），此前 HID 通道 err=2 系走错通道。协议层零改动（输入源编码与 BuildFrame 帧格式被 StandardSource_E 枚举二次证实一致）。bRequest 常量系混淆代码推断（0xA8/0xA9 写、0xA3 读），ProbeDdc 自动遍历 5×3 候选组合锁定，真机验证待用户执行 `rawusb`。
---
### 对比版本：0.4.2 VS 0.4.1
- 对比生成时间：2026-10-15 16:00
- 新增内容：hid 深度诊断（多模式试开 + UTF-16 hex + 对照组 + 错误码语义）；多模式试开辅助函数 TryOpenReport/WinErrName/StringHex
- 修改内容：RunHidDiag 全量枚举 HID 接口分组目标/对照；EnumerateHidInterfaces 支持空 vid/pid 全量枚举
- 删除内容：无
- 核心影响说明：本地键盘验证揭示 2 条规律——零访问模式可绕过设备独占、err=2=路径格式问题/err=5=被独占。真机 AOC err=2 需深度诊断区分"路径问题 vs 驱动状态问题"。协议层零回归。
---
### 对比版本：0.4.1 VS 0.4.0
- 对比生成时间：2026-10-15 15:30
- 新增内容：无
- 修改内容：修复 3 个 bug——HID 接口 GUID 写错（实为 MODEM 类 GUID，致枚举恒空）、SetupDiGetDeviceInterfaceDetailW cbSize 误填 buffer 总长、DevicePath 偏移量误用 cbSize 致路径截断；删除 DirectHidChannel 重复 GetPreparsedData 调用（内存泄漏）；改为运行时 HidD_GetHidGuid 获取 GUID
- 删除内容：无
- 核心影响说明：本地键盘设备决定性验证通过——成功枚举 HID 接口、符号链接完整正确、CreateFile 正确调用（err=5 被系统独占属预期）。整条"枚举→解析路径→CreateFile→hid.dll I/O"链路已被证实可用，协议层零回归。待真机验证 AOC MI_02 通道（厂商私有 DDC，不被独占，期望 CreateFile 成功）。
---
## ddc.exe 版本迭代记录（新工具，与 AocInputSwitcher 并行）
### 版本号：ddc 1.0.0
- 编译时间：2026-10-18 00:48
- 环境配置：Windows 自带 csc（`C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe`）；工作目录 `D:\Ai\G-Menu-deepseek\ddc`；编译命令 `ddc\build.bat`（`/target:exe /platform:anycpu /optimize+ /codepage:65001`）
- 编译结果：编译成功（EXIT=0，零 error 零 warning；产物 `ddc\ddc.exe` 19,456 字节，AnyCPU）
- 需求来源：用户要求“重新反汇编 G-Menu.exe，分离出一份新的仅用 DDC 控制的 ddc.exe 控制台；全程通过 DP1 接口的 DDC 信号通路控制 HDMI1/HDMI2/DP1/DP2 输入源切换”
- 本次变更概要：
  1. 新增：
     - `ddc/ddc.cs`：单文件控制台程序，仅用 Windows 标准 DDC/CI（dxva2 物理监视器 MCCS API）经显示链路（DP1 的 DDC 通道）下发 VCP 0x60 输入源切换；不依赖 USB 上行线、厂商 DLL 或 G-Menu
     - `ddc/build.bat`、`ddc/build.ps1`：一键编译脚本（系统自带 csc.exe，AnyCPU）
     - `ddc/README.md`：原理、逆向依据、命令、编译、前提限制、排错、真机试跑步骤
     - 命令：`dp1/dp2/hdmi1/hdmi2`（切换）、`query`（读 VCP 0x60）、`list`（显示器/物理监视器枚举）、`diag`（MCCS 能力位 + VCP 读取）、`test`（离线自检）、`help`；选项 `--monitor N`（多显示器）
  2. 修改：无（AocInputSwitcher 源码/文档未改动）
  3. 删除：无
  4. 兼容说明：新工具独立目录，与 AocInputSwitcher 并行、互不依赖；退出码 0=成功 1=用法错误 2=未找到显示器 3=DDC 读写失败 4=自检失败
- 逆向依据（本轮复核）：
  - `decompiled/Zeasn.Equipment.Entity.Lib/Zeasn.Equipment.Entity.Lib.decompiled.cs:10679`：`OP_60_InputSource`（VCP 0x60 输入源）
  - 同文件 `:11063`：`StandardSource_E`（DP1=15/0x0F、DP2=16/0x10、HDMI1=17/0x11、HDMI2=18/0x12、USB_C1=21、USB_C2=22）
  - `G-Menu_反汇编总结.md` §4：DDC/CI 帧格式与 XOR 校验（`ddc.exe test` 逐字节比对）
- 验证记录：
  - `ddc.exe test`：5 通过 / 0 失败（DP1/DP2/HDMI1/HDMI2/查询帧与 G-Menu 反编译逐字节一致）
  - `ddc.exe list`（开发机）：正确枚举虚拟显示器 `\\.\DISPLAY49`（Generic Non-PnP Monitor），物理监视器枚举成功
  - `ddc.exe diag` / `query` / `hdmi1`（开发机无真实 DDC 显示器）：按设计优雅失败（Win32 50 不支持该请求），退出码 3，不崩溃，并输出排查建议
  - `cmd /c build.bat`：重建成功（EXIT=0）
  - 真机待验证（用户环境 AG275UXM2 经 DP1 连接）：`list` 识别 AOC → `query` 读到当前输入源 → `hdmi1/hdmi2/dp2` 切换生效
- 备注：DDC 传输层为 Windows MCCS（dxva2），与 G-Menu 的 USB 桥通道（RTK HID / ENE WinUSB）物理通道不同、VCP 语义一致；真机若固件拒绝主机侧 VCP 0x60 写入，可回退 AocInputSwitcher（USB 通道）或显示器按键。

---
### 版本号：ddc 1.1.0
- 编译时间：2026-10-18 01:10
- 环境配置：同上（Windows 自带 csc，AnyCPU）；工作目录 `D:\Ai\G-Menu-deepseek\ddc`
- 编译结果：编译成功（EXIT=0，零 error 零 warning）
- 变更原因：用户真机首测（AG275UXM2，DP1 接 PC，显示器当前在 HDMI2）——`list` 已识别 AOC（`MONITOR\AOCA526`）、`query` 读到 VCP 0x60=0x12(HDMI2)（证明显示器在 HDMI2 时 DP1 的 DDC 仍可读），但连续切换命令后回读仍为 0x12，且伴随 DDC 瞬时错误；需要更明确地判断“显示器是否真的切换”
- 本次变更概要：
  1. 新增：
     - 写后自动回读确认：切换命令发出后轮询 VCP 0x60（0.25s × 20，约 5s），输出 `[OK] 已确认切换` / `[WARN] 回读仍为旧值，未确认` / `[WARN] 回读失败`，用于区分“固件接受但忽略”与“已切换”
     - `watch` 命令：每 0.5s 回读 VCP 0x60 并打印变化（Ctrl+C 退出），便于观察切换过程
     - DDC 读/写重试：读 3 次（间隔 250ms）、写 3 次（间隔 300ms），吸收输入切换前后的瞬时 DDC 错误
  2. 修改：
     - 错误输出统一为十六进制 Win32 码 + 系统消息（如 `0xC0262589 ...`）
     - `diag`：MCCS 能力查询失败时补充说明“常见于 EDID/驱动未声明 MCCS 版本，不影响 VCP 读写，可忽略”
     - 帮助与 README 同步（version 1.1.0、watch、回读确认语义、错误码表）
  3. 删除：无
  4. 兼容说明：命令与协议语义不变；`test`/`list`/`query`/`diag` 行为向后兼容
- 验证记录（开发机）：
  - 编译成功；`ddc.exe test` 5/5
  - `watch` 冒烟：对虚拟显示器循环回读，按设计输出回读失败并退出，无崩溃
  - 真机待验证（用户环境）：`ddc.exe dp1`（显示器在 HDMI2 时）期望回读 0x0F 并显示 `[OK] 已确认切换`；若显示 `[WARN] 回读仍为 0x12`，则判定该固件不接受主机侧 VCP 0x60 切换

---
### 版本号：ddc 1.2.0
- 编译时间：2026-10-18 01:40
- 编译结果：编译成功（EXIT=0）
- 变更原因：用户确认显示器 **USB 上行线未连接**，PC 仅有 DP1（NV 独显）与 HDMI2（Intel 核显）两条视频线；G-Menu 在显示器处于 HDMI1 时仍可切换 → 说明其走的是视频线 DDC（G-Menu 自带 `DDCHelperLib.dll` 即 dxva2 物理监视器 API）。此前工具仅选“第一个 AOC 显示器”，可能选中了核显链路（驱动对 VCP 0x60 写入行为可能不同）
- 本次变更概要：`RunSwitch`/`RunQuery` 改为**遍历所有枚举到的显示器**（AOC 优先，其余随后），逐个尝试读写并在切换后回读确认，命中即返回；`--monitor N` 仍可强制指定；`list` 不变
- 验证记录（开发机）：编译成功；`test` 5/5；`query` 对虚拟显示器按候选顺序尝试后优雅失败（退出码 3）
- 真机待验证：分别在显示器处于 DP1 / HDMI2 / HDMI1 时运行 `ddc.exe list` 与 `ddc.exe dp1`/`hdmi2`，观察“检测到 N 个显示器”与 `[OK]/[WARN]` 行

---
### 版本号：ddc 1.2.0 真机验证 + new_ddc 1.0.0（最终版）
- 验证时间：2026-10-18 01:49（用户真机：AG275UXM2，DP1 接 NV 独显，HDMI2 接 Intel 核显，USB 上行线未连接）
- 真机结果：
  - `ddc.exe list`：1 个显示器，`MONITOR\AOCA526`，自动识别 AOC（Generic PnP Monitor 驱动）
  - `ddc.exe dp1`（从 HDMI2）→ 回读 0x0F，确认切换
  - `ddc.exe dp2`（从 DP1）→ 回读 0x10，确认切换（DP2 无设备也能切，与 G-Menu 行为一致）
  - `ddc.exe hdmi1` / `hdmi2` → 回读 0x11 / 0x12，多次验证成功
  - 切换瞬间偶发 `[WARN] 回读失败`：显示器切源时当前链路 DDC 短暂中断，命令已生效（下一条命令读到新输入源为证）
  - 用户确认“可以使用，切换正常”
- 新增：
  - `ddc/new_ddc.cs` → 产物 `ddc/new_ddc.exe`：参数 `1-4`（1=HDMI1 2=HDMI2 3=DP1 4=DP2），执行后立即退出；输出 `OK <源名>`（或 `OK <源名> (no ack)`）/`NO MONITOR`/`FAIL <源名>`；退出码 0/1/2/3
  - 快速切换逻辑：AOC 优先遍历显示器、写 VCP 0x60 重试 3 次、约 1.2s 回读确认（确认失败也按“已发送”返回）
- 修改：
  - `build.bat`：改为纯 ASCII + CRLF（修复中文/括号导致的 cmd 解析异常），一次编译 ddc.exe 与 new_ddc.exe
  - `build.ps1`：改为纯 ASCII（兼容 Windows PowerShell 5.1 对无 BOM UTF-8 的解析），一次编译两个 exe
  - `ddc/README.md`：新增 new_ddc.exe 用法、真机验证结论（§10）
- 验证记录（开发机）：
  - `cmd /c build.bat`、`powershell -File build.ps1` 均编译成功（EXIT=0）
  - `ddc.exe test` 5/5；`new_ddc.exe`（无参数/非法参数/无 DDC）分别返回 1/1/3，行为正确
- 结论：纯视频线 DDC（dxva2/MCCS）即可完成 HDMI1/HDMI2/DP1/DP2 切换，无需显示器 USB 上行线与厂商 DLL；G-Menu 在 HDMI1 状态下能控制亦为同一视频线 DDC 通道（其 `DDCHelperLib.dll` 即 dxva2 实现）

---
### 版本号：new-ddc.exe 1.0.0（正式版）
- 编译时间：2026-10-18 03:15
- 编译结果：编译成功（EXIT=0；产物 `ddc/new-ddc.exe` 15,360 字节，AnyCPU，别名 `new_ddc.exe`）
- 需求（用户正式版规范）：
  1. 参数 `1|2|3|4` = 直接发送切换信号 `0x11|0x12|0x0F|0x10`，静默、无输出、执行完退出（不查询当前输入源）；
  2. 无参数运行：读当前输入源后自动切换——`0x12`→`0x0F`；`0x0F`→`0x12`；`0x11`/`0x10`/其它→`0x0F`；静默退出；
  3. `debug1|debug2|debug3|debug4|debuglist`：显示完整调试信息；
  4. `help`：显示完整规则。
- 实现（`ddc/new-ddc.cs`）：
  - 静默模式：直接 `SetVCPFeature(VCP 0x60)`，写入重试 3 次；多显示器 AOC 优先逐个尝试；**不输出任何内容**
  - 无参数模式：读 `VCP 0x60`（重试 2 次）→按规则选择目标后写入；读不到时默认切 `0x0F`（DP1）
  - 调试模式：输出与 `ddc.exe` 一致（候选显示器 / 当前输入源 / 已发送 / 回读确认或 WARN）
  - 退出码：`0`成功 `1`参数错误 `2`未找到显示器 `3`DDC 写入失败
- 构建：`build.bat` / `build.ps1` 更新为编译 `ddc.exe` + `new-ddc.exe`，并复制 `new_ddc.exe` 别名
- 验证（开发机）：`help` 正常；`new-ddc.exe 3` 与无参数模式均输出长度为 0 且退出码 3（无真实 DDC）；`debug3` 输出完整调试信息、退出码 3；`debuglist` 正常；非法参数退出码 1
- 真机：用户已确认 `ddc.exe` 切换正常（DP1 视频线 DDC 通道），`new-ddc.exe` 使用同一通道与同一 VCP 0x60 协议

---
### 版本号：aoc-switch 3.4.0（最终交付）
- 编译时间：２０２６－１０－１９　０５：４４（v3.4）；提交准备：２０２６－１１－１８
- 环境配置：Windows 自带 `csc.exe`，`/platform:x86 /target:winexe /win32icon:icon.ico`；运行时须将 `DDCHelperLib.dll` 与 exe 放在同一目录（从 G-Menu 安装目录 `resources\bin` 复制）
- 编译结果：编译成功（零错误），产物 ４８８，４４８ 字节
- 本次版本变更概要：
  １. 新增：
     - `ddc/aoc-switch.cs`：三通道切换工具，按「DDCHelperLib → NVAPI → dxva2」顺序自动择路；支持静默切换 `1｜2｜3｜4`、无参自动切换、`diag`、`list`、`debug1..4`。
     - 按需控制台：编译为 `winexe`，静默切换不出现黑色窗口；仅在缺库、`diag`、`list`、`debug*` 与 `help` 时附加父控制台或新建窗口。
     - 根目录 `README.md`（以全角标点梳理代码与结构）与 `.gitignore`（隔离第三方安装包、反编译产物与二进制）。
  ２. 修改：
     - 修正 `DDCHelperLib` 返回值语义：函数返回 `0` 即成功，`DdcValue.Result` 为当前值，`DdcValue.Value` 为 VCP 最大值；早期版本误判为「`Result==0` 才成功」，导致有效读数被丢弃。
     - 切换路径补上 `EnumDisplayIDIni` 显示器枚举初始化，解决「diag 可读、切换不可读」的根因。
     - `Set` 请求补上 `Cmd[1] = VCP`，与 `Get` 请求对称。
     - 库查找改为只读运行目录，缺失时显示下载地址并等待回车；写入 ACK 即视为成功。
  ３. 删除：`aoc-lib.txt` 缓存读写、进程模块查找、注册表查找与全盘磁盘扫描。
  ４. 兼容说明：协议层零改动，仍为标准 DDC／CI VCP `0x60`；席位１原有的 dxva2 通道保留为第三顺位回退，两席位共用同一份 exe。
- 验证记录：
  - 席位２：`diag` 显示 index ０ 当前值 `0x12`（HDMI２）；`debug3` 写入 `rc=0x00000000`，回读确认为 `0x0F`（DP１），屏幕实际切换。
  - 席位１：`diag` 显示 index ０ 当前值 `0x0F`（DP１），dxva2 同步可读。
  - 构建：`build.bat` 一次编译三套工具；静默模式实测无窗口且退出码正确；缺库提示实测正常。
  - `git add -n .` 预演：仅 ２６ 个源码、脚本、文档与配置文件进入提交清单。
- 备注：项目完成。仓库仅提交自研源码、构建脚本与文档；`GMeun/`、`decompiled/`、`*.exe`、`*.dll`、`*.ico` 及硬件日志由 `.gitignore` 排除。

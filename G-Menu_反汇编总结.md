# G-Menu / AocInputSwitcher 反汇编总结（REA + Ghidra）

> **文档目的**：完整记录 `G-Menu.exe`（及内置 `RhHidAPI.dll`、.NET 后端）的反汇编结果、DDC/CI 输入源协议与关键结论。供**后续新会话**直接读取、复用、继续开发 `AocInputSwitcher`。
>
> **适用范围**：`d:\Ai\G-Menu` 工作目录。相关项目：`AocInputSwitcher/`（自研工具）、`decompiled/`（.NET 反编译）、`GMeun/`（G-Menu 安装包）。
>
> **最后更新**：2026-10-14。来源：REA v6.5.0 + Ghidra 12.1.4，证据存档见文末「证据文件清单」。

---

## 0. 一句话结论

`AocInputSwitcher` 与官方 G-Menu **走完全相同的物理通道**：经显示器 USB 上行线的 Realtek USB-I2C 桥（`VID_05F6&PID_AC2F` 的 **MI_02 HID** 接口），通过**同一份 `RhHidAPI.dll`**（SHA256 字节级一致）对显示器执行标准 **DDC/CI VCP 0x60（输入源）** 读写。当前开发机报 `device not found` 的**根因是无显示器**（桥设备不存在），**代码无 bug**，属协议预期行为。

---

## 1. REA 工具链环境（后续会话可直接复用）

### 1.1 组件与路径

| 组件 | 版本 | 路径 |
|---|---|---|
| REA (rea-agents) | 6.5.0 | `C:\Users\Administrator\AppData\Roaming\npm\node_modules\rea-agents` |
| REA 技能 | v42（207 个 MCP 工具） | `.agents\skills\reverse-engineer-anything`、`.trae\skills\reverse-engineer-anything` |
| Ghidra | 12.1.4 PUBLIC | `D:\Tools\Ghidra12\ghidra_12.1.4_PUBLIC` |
| JDK | OpenJDK 21.0.12.1（Temurin） | `D:\Tools\jdk21\jdk-21.0.12.1+1` |

### 1.2 环境变量（Ghidra provider 可用前提，**新会话需重新设置**）

```powershell
$env:GHIDRA_INSTALL_DIR = "D:\Tools\Ghidra12\ghidra_12.1.4_PUBLIC"
$env:JAVA_HOME = "D:\Tools\jdk21\jdk-21.0.12.1+1"
$env:PATH = "D:\Tools\jdk21\jdk-21.0.12.1+1\bin;$env:PATH"
# 验证
rea doctor --client codex --json      # 或 providers / providers_now3.json
```

> **注意**：`GHIDRA_INSTALL_DIR` 未设置时，`rea analyze --provider ghidra` 会返回 `provider_unavailable / not_configured`。终端环境变量不跨会话继承，**新会话务必重设**后再分析。
> 另外：本会话未注册 REA MCP 服务器（`integrated_code_mode`/`mcp_gitnexus` 外无 REA）。本会话统一用 **REA CLI**，非 MCP。

### 1.3 常用 REA CLI 命令

| 命令 | 用途 | 示例 |
|---|---|---|
| `rea analyze <path> --provider ghidra --format json` | 目标概览（段、函数数、字符串数） | `rea analyze 目标.dll --provider ghidra --format json` |
| `rea decompile <path> <函数名/地址> --provider ghidra --format json` | 单函数伪码 | `rea decompile 目标.dll RH_HidI2CWrite --provider ghidra --format json` |
| `rea search <path> <pattern> --kind procedures --mode regex --provider ghidra --format json` | 搜索函数名/字符串 | 列出导出函数 |
| `rea function <path> <函数名> --provider ghidra --format json` | 单函数完整证据 | |
| `rea providers` | 列出分析 provider 可用性 | |
| `rea doctor` | 环境诊断 | |
| `rea inspect-managed-members <path> --format json` | PE/CLI 托管元数据成员（用于 .NET 后端） | |

> REA CLI 的 `open_binary` 是 **MCP 工具名**，不是 CLI 子命令；CLI 用 `analyze`/`decompile`/`search`/`function` 等，均带 `<path>` 参数。

---

## 2. G-Menu 整体架构

`G-Menu.exe`（约 110MB）是 **64 位 Electron/Chromium 外壳**。真正做显示器控制的是三层，反汇编逐层覆盖：

```
GMeun/
├── G-Menu.exe                      # 64位 Electron 壳（Chromium + Node）
├── resources/
│   ├── app.asar / resources.pak    # 前端（HTML/JS/CSS，UI 壳）
│   ├── elevate.exe                 # UAC 提权辅助
│   └── bin/                        # .NET 后端 + 原生库（核心）
│       ├── G_Menu.exe              # .NET 后端入口
│       ├── Zeasn.*.dll             # Zeasn 程序集（Monitor/DTS/USB/Framework 等）
│       ├── RhHidAPI.dll            # ★ Realtek HID 桥（x86 原生，RTKUSBSDKDriverLib/32bitrelease/）
│       ├── DDCHelperLib.dll        # DDC 辅助库
│       ├── LED_Control.dll         # 灯效控制
│       ├── nircmd.exe              # 系统控制
│       ├── KeraLua.dll / lua54.dll  # Lua 脚本
│       └── RTKUSBSDKDriverLib/32bitrelease/RhHidAPI.dll   # RhHidAPI 实际位置
└── locales/                        # 多语言
```

- **前端（app.asar）**：Electron UI，调用 `SetSource`，非协议关键（薄封装）。
- **.NET 后端（resources/bin）**：`G_Menu.exe` + `Zeasn.Monitor.Lib`（显示/输入源逻辑）+ `Zeasn.Framework.Core.Lib`（门面）等。反编译源码在 `decompiled/`。
- **原生桥（RhHidAPI.dll）**：`MI_02 HID` 的底层 I2C 驱动封装。**本工具与它同源、字节级一致**。

---

## 3. RhHidAPI.dll 反汇编（核心，Ghidra）

### 3.1 目标身份

| 项 | 值 |
|---|---|
| 路径（G-Menu 内置） | `D:\Ai\G-Menu\GMeun\resources\bin\RTKUSBSDKDriverLib\32bitrelease\RhHidAPI.dll` |
| 位数 | **32 位（x86）原生库** |
| 大小 | 358296 字节 |
| SHA256 | `733CB268D72558BF1A4EB0757AB9943E66C6E65707C18464378E1623BFE6D725` |
| 与 AocInputSwitcher 自带是否一致 | **一致（字节级相同）** |

> 因是 32 位原生库，**控制程序必须 x86 编译**，且 `RhHidAPI.dll` 与 exe 同目录。

### 3.2 导出函数表（Ghidra 完整恢复，16 项）

| 函数 | 地址 | 说明 |
|---|---|---|
| `RH_CloseDev` | 0x100180e0 | 关闭设备 |
| `RH_ReadBinVersion` | 0x10018200 | 读固件版本 |
| `RH_ReadDevVersion` | 0x10018440 | 读设备版本 |
| `RH_UpdateDev` | 0x100185a0 | 更新设备 |
| `RH_ListAllHidDev` | 0x10018860 | 枚举 HID（官方，真机曾 count=0） |
| `RH_ListAllHidDevByVIDPID` | 0x100189c0 | 按 VID/PID 过滤枚举 |
| `RH_OpenHid` | 0x10018b50 | 打开 HID（入参 DevInst，返回 pDev） |
| `RH_FreeDevList` | 0x100190f0 | 释放设备列表 |
| `RH_ConfigLog` | 0x10019120 | 配置日志 |
| `RH_HidI2CConfig` | 0x100191a0 | 配置 I2C（从机地址等） |
| `RH_HidI2CWrite` | 0x10019280 | I2C 写（★ 切输入源） |
| `RH_HidI2CRead` | 0x10019350 | I2C 读（★ 查输入源） |
| `RH_HidI2CBlockWriteFlash` | 0x10019420 | 块写闪存 |
| `RH_HidI2CBlockEraseFlash` | 0x100195b0 | 块擦闪存 |
| `RH_HidI2CBlockStatus` | 0x10019710 | 块状态 |
| `RH_MST_HidI2CBlock` | 0x10019890 | MST 块 I2C |

### 3.3 关键函数实现（Ghidra 伪码提炼）

**设备对象模型**：`pDev` 指向一个带 **vtable** 的设备对象。内部 I2C 方法位于 vtable：
- `vtable[0x9c](bySlaveAddr, memLen, speed)` = I2C Config
- `vtable[0xa0](dwAddr, pBuf, wLen)` = I2C Write
- `vtable[0xa4](dwAddr, pBuf, wLen)` = I2C Read

> **核心结论**：`RhHidAPI.dll` 是**薄封装**，只做校验 + 类型检查 + 虚调用；**不做任何 DDC 字节拼装**。0x6E（从机）/0x51（寄存器）由参数承载，DDC payload 原样透传给内部 I2C 驱动。

各函数伪码要点：

**`RH_HidI2CConfig`（0x100191a0）**
```
校验: pDev≠null, bySlaveAddr≠0, bySpeed≤2, byMemLen≤4
类型检查: FUN_1001d03a(pDev) + type_info_compare
虚调用: vtable[0x9c](bySlaveAddr, byMemLen, bySpeed)
返回: 0=成功 / 非0=I2C Config Fail(ErrorCode)
```

**`RH_HidI2CWrite`（0x10019280）**
```
校验: pDev≠null, pBuf≠null, wLen≤0x80(128)
类型检查
虚调用: vtable[0xa0](dwAddr, pBuf, wLen)
返回: 0=成功 / 非0=I2C Write Fail(ErrorCode)
```

**`RH_HidI2CRead`（0x10019350）**
```
校验: pDev≠null, pBuf≠null, wLen≤0xC0(192)
类型检查
虚调用: vtable[0xa4](dwAddr, pBuf, wLen)
返回: 0=成功 / 非0=I2C Read Fail(ErrorCode)
```

**`RH_ListAllHidDevByVIDPID`（0x100189c0）**
```
签名: (uint vid, uint pid, DevList* list, uint* count)
调用内部枚举 FUN_10009fc0(list, vid, pid, out, count)
成功打印 "List Device Success,Hid Count is %d"
失败打印 "List Hid Device Fail"
```
> 枚举依赖物理设备存在。**无显示器 → 返回 0 → "device not found"。这就是当前问题根因。**

### 3.4 精确 P/Invoke 签名（与 C# 一致）

| 函数 | 调用约定 | 签名 |
|---|---|---|
| `RH_OpenHid` | Cdecl | `IntPtr RH_OpenHid(uint dwDevInst)` |
| `RH_CloseDev` | Cdecl | `void RH_CloseDev(IntPtr pDev)` |
| `RH_ListAllHidDev` | Cdecl | `int RH_ListAllHidDev(ref IntPtr pp, ref uint count)` |
| `RH_FreeDevList` | Cdecl | `int RH_FreeDevList(IntPtr p)` |
| `RH_ConfigLog` | StdCall | `bool RH_ConfigLog(bool bEnableLog, int nLevel)` |
| `RH_HidI2CConfig` | StdCall | `int RH_HidI2CConfig(IntPtr pDev, byte slave, byte memLen, byte speed)` |
| `RH_HidI2CWrite` | StdCall | `int RH_HidI2CWrite(IntPtr pDev, uint dwaddr, byte[] buf, ushort len)` |
| `RH_HidI2CRead` | StdCall | `int RH_HidI2CRead(IntPtr pDev, uint dwaddr, byte[] buf, ushort len)` |

> 所有 `RH_HidI2C*` 返回 **0=成功**；`RH_OpenHid` 返回**非零 pDev=成功**，0=失败。

---

## 4. DDC/CI 输入源协议（VCP 0x60）

### 4.1 帧格式

**完整软件帧**（首两字节为 `slave=0x6E, reg=0x51`），XOR 校验：
- 切换（Set VCP）：`6E 51 84 03 60 <Hi> <Lo> <CHK>`
- 查询（Get VCP）：`6E 51 82 01 60 <CHK>`

**校验**：`CHK = 帧首字节到校验字节前所有字节的逐字节异或`。

> 实际下发给 `RH_HidI2CWrite` 的是 **完整帧去掉前 2 字节（6E 51）** 的 payload；寻址（从机 0x6E / 寄存器 0x51）已由 `Config`/参数承载，DLL 不重复拼接。

`Program.cs` 的 `Monitor.BuildFrame` 逻辑：
```
f[0]=SLAVE_ADDR(0x6E); f[1]=REG_ADDR(0x51);
f[2]=(byte)(0x80 + n + 1);            // n=命令字节数
f[3] = isGet ? 0x01 : 0x03;
f[4..]=commands;
CHK = XOR(f[0 .. n+3]);
```
- Get：n=1 → 帧头 `82 01`
- Set：n=3 → 帧头 `84 03`

### 4.2 输入源编码表

| 源 | 编码 | 完整帧（Set） | 实际下发（去 6E 51） |
|---|---|---|---|
| DP1 | 0x0F | `6E 51 84 03 60 00 0F D7` | `84 03 60 00 0F D7` |
| DP2 | 0x10 | `6E 51 84 03 60 00 10 C8` | `84 03 60 00 10 C8` |
| HDMI1 | 0x11 | `6E 51 84 03 60 00 11 C9` | `84 03 60 00 11 C9` |
| HDMI2 | 0x12 | `6E 51 84 03 60 00 12 CA` | `84 03 60 00 12 CA` |
| USB-C1 | 0x15 | — | — |
| USB-C2 | 0x16 | — | — |
| 查询 | — | `6E 51 82 01 60 DC` | `82 01 60 DC` |

> 校验和已逐字节验证通过（如 HDMI1 全帧 XOR = C9；查询全帧 XOR = DC）。

### 4.3 读时序与应答

- 写后 `Sleep(150ms)`。
- 读缓冲 32 字节，读前 `buf[0] = 0x6F`；`RH_HidI2CRead(pDev, 0x51, buf, 32)`。
- 外层最多重试 3 次（`IO_RETRY=3`，间隔 `2*attempt` ms）。
- **应答识别**：缓冲前 16 字节扫描 `buf[i]==0x6E && (buf[i+1]&0x80)!=0` 的帧头。
- **查询应答布局**（Get VCP Feature Reply）：
  ```
  6E 8F 60 [result] [type] [maxH][maxL] [valH][valL] [chk]
                                └ max ┘    └ value ┘
  ```
  帧头 +5/+6 = max，+7/+8 = value；兼容旧设备 `idx=buf[1]-0x80` 的动态偏移。

### 4.4 官方 .NET 调用链（来自反编译）

```
pDev = RH_OpenHid(dwDevInst)
RH_HidI2CConfig(pDev, 0x6E, 1, 0) == 0          # 0x6E 从机; speed=0
WriteCycleThree(pDev, 0x51, 帧[2..], 帧长-2)     # 3次重试 2ms/4ms
Sleep(约150ms)
读 buf[0]=0x6F; RH_HidI2CRead(pDev, 0x51, buf, 32)
RH_CloseDev(pDev)
```
- `WriteCycleThree(pDev, 81, buf, len)`：81=0x51，3 次重试。
- `ReadCycleThree / ReadCycleThreeAndCheck`：读 + 首字节校验。
- 另有 `RH_HidI2CConfig(pDev, 0x94, 1, 1)`（148=0x94）用于**其它机型**（非本 AOC 桥）。

---

## 5. .NET 后端反编译（`decompiled/`）

| 程序集 | 文件 | 关键内容 |
|---|---|---|
| G_Menu | `decompiled/G_Menu/G_Menu.decompiled.cs` | `SetSource(string)` 入口 → `DisPlayBase.SetSource` |
| Zeasn.Monitor.Lib | `decompiled/Zeasn.Monitor.Lib/...decompiled.cs` | **核心**：`RTKRTKApi`（P/Invoke）、`HubRTK.SetDDC`、DDC 帧层 |
| Zeasn.Framework.Core.Lib | `...` | `DisPlayBase` 静态 API 门面 |
| Zeasn.Equipment.Entity.Lib | `...` | 设备实体 |

关键方法（`RTKRTKApi` / `SetDDC`）：
- `SetDDC(int hubScalerType, string id, params byte[] commands)`：按 `ScalerType.RTK/MTK/other` 分派。
- RTK 路径：`HnlG01AgZg.SetDDC(ScalerType.RTK, id, commands)` → `RTKRTKApi.WriteCycleThree/ReadCycleThree`。
- P/Invoke 列表与上文 §3.4 一致（`RH_HidI2C*` 全 StdCall，`RH_OpenHid`/`CloseDev`/`ListAllHidDev`/`FreeDevList` 全 Cdecl）。

> 成员清单（含全部类型/方法）：`D:\Ai\rea-analysis\gmenu_members.json`（18MB）、`mon_lib_members.json`（17MB）。

---

## 6. 关键结论与陷阱（务必记住）

1. **通道唯一**：输入源切换**只能走 MI_02 HID**（Realtek USB-I2C 桥）。
   - **MI_00** 是 `winusb.sys` 驱动的 **"AGON Light FX Device" 灯效控制器**，发 DDC（0xF6/0xC6/0xD6）一律 `err=31`、无应答。
   - 纯 `hid.dll` 直开 MI_02 标准路径曾失败（`err=2 FILE_NOT_FOUND`）；官方正是在 hid.dll 外封装了 RhHidAPI 的枚举/打开逻辑。
2. **必须 x86**：RhHidAPI.dll 是 32 位，进程 64 位会 `BadImageFormat`。
3. **设备必须物理连接**：无显示器 → 无桥设备 → 枚举 0 → `device not found`（**预期行为，非 bug**）。
4. **完全退出 G-Menu**（含后台托盘），否则占用；必要时**右键以管理员运行**。
5. **枚举策略**：`RH_ListAllHidDev` 真机曾返回 `count=0`（但 G-Menu 能用），故本工具**不依赖它**，改用 **SetupAPI**（`SetupDiGetClassDevs + DIGCF_ALLCLASSES|DIGCF_PRESENT`）自枚举 `VID_05F6&PID_AC2F` 节点，按优先级（HID MI_02 > USB MI_02 > 其它 MI_02 > 任意 HID > 父设备）逐个 `RH_OpenHid → Config → DDC 探测`，首个回 `6E/8F` 即锁定。

---

## 7. 证据文件清单

**REA 原生分析（`D:\Ai\rea-analysis\`）**：
| 文件 | 内容 |
|---|---|
| `rhhid_analyze.json` | RhHidAPI.dll 概览（段、1839 函数、979 字符串） |
| `rhhid_procedures.json` | 导出函数表（16 项 + 地址） |
| `rhhid_config.json` | `RH_HidI2CConfig` 伪码 |
| `rhhid_write.json` | `RH_HidI2CWrite` 伪码 |
| `rhhid_read.json` | `RH_HidI2CRead` 伪码 |
| `rhhid_open.json` | `RH_OpenHid` 伪码 |
| `rhhid_list.json` | `RH_ListAllHidDevByVIDPID` 伪码 |
| `providers_now3.json` | provider 可用性（Ghidra available / 12.1.4 / JDK 21） |
| `gmenu_members.json` / `mon_lib_members.json` | .NET 成员清单（各 ~18MB） |
| `gmenu_interesting.txt` / `mon_lib_interesting.txt` | 关键类筛选 |

**工作目录**：
| 路径 | 内容 |
|---|---|
| `decompiled/` | 4 个 .NET 程序集反编译 C# |
| `AocInputSwitcher/` | 自研工具（`Program.cs`、`RhHidAPI.dll`、`协议说明.md`、`使用说明.txt`、`AocInputSwitcher.exe`） |
| `AocInputSwitcher/协议说明.md` | 协议说明（官方 RhHidAPI 通道版，含真机拓扑与排查） |

---

## 8. 待办 / 后续方向

1. **开发机离线验证**（当前无显示器，无法真机切源）：
   - 拟新增 `test` 命令：构建全部 DDC 帧 + 校验 XOR + 打印 hex，**不加载 RhHidAPI.dll**，纯 C# 验证协议逻辑。
   - 拟新增 `list` 命令：SetupAPI 枚举全部 present 设备，高亮 `05F6/AC2F`，**不加载 32 位 DLL**。
   - （以上需用户确认后再编码，详见上一轮《代码任务执行拆解方案》。）
2. **真机验证**：连上显示器 USB 上行线后运行 `hdmi1/hdmi2/dp1/dp2/query/diag`，确认切换有效。
3. （可选）分析 Electron 前端 `app.asar`/`resources.pak`（UI 壳，非协议关键，按需）。

> **后续会话快速上手**：重设 §1.2 环境变量 → 读 §3（RhHidAPI）+ §4（DDC 协议）即可对齐全部协议细节；改代码务必遵循项目「代码任务执行拆解方案 + 用户确认」与「版本迭代记录（AI开发功能说明.md）」规则。

---

## 9. 分离产物：ddc.exe（仅 DDC 控制，经 DP1 的 DDC 信号通路）

> 2026-10-18 追加；对应目录 `ddc/`（源码 `ddc/ddc.cs`，产物 `ddc/ddc.exe`，编译脚本 `ddc/build.bat` / `build.ps1`，说明 `ddc/README.md`）。

### 9.1 落点

按需求“从 G-Menu 反汇编中分离出仅用 DDC 控制的 ddc.exe 控制台”，该工具只做一件事：向显示器发送标准 DDC/CI **VCP 0x60（输入源）**，切换 HDMI1 / HDMI2 / DP1 / DP2。

- 输入源编码复核：`decompiled/Zeasn.Equipment.Entity.Lib/Zeasn.Equipment.Entity.Lib.decompiled.cs:11063` 的 `StandardSource_E`（DP1=15、DP2=16、HDMI1=17、HDMI2=18）；帧格式与校验见 §4，`ddc.exe test` 已逐字节对齐。
- 传输层：Windows 标准 **MCCS / 物理监视器 API**（`dxva2.dll`：`EnumDisplayMonitors`/`GetMonitorInfo` → `GetPhysicalMonitorsFromHMONITOR` → `SetVCPFeature` / `GetVCPFeatureAndVCPFeatureReply`），由显示驱动沿 DP1 链路的 DDC/AUX 通道下发；与 G-Menu 的 USB 桥通道（§3–§4 的 RTK HID / ENE WinUSB）物理通道不同、VCP 语义相同。因此不加载 `RhHidAPI.dll` / `EneEc.dll`，不依赖 USB 上行线，也无需退出 G-Menu。
- 帧格式（§4）仅用于离线对账（`ddc.exe test`）；运行时由驱动按 MCCS 规范封装同义 DDC/CI 帧。

### 9.2 真机验证结果（2026-10-18）

环境：AG275UXM2；PC 经 **DP1（NV 独显）** 与 **HDMI2（Intel 核显）** 连接显示器；显示器 USB 上行线未连接。

1. `ddc.exe list` → 识别 1 个显示器 `MONITOR\AOCA526`（AOC 自动选中）；
2. `ddc.exe dp1`（从 HDMI2）→ 回读 0x0F，确认切换；`ddc.exe dp2`（从 DP1）→ 回读 0x10，确认切换（DP2 无设备亦可切，与 G-Menu 一致）；
3. `ddc.exe hdmi1` / `hdmi2` → 回读 0x11 / 0x12，多次成功；
4. 切换瞬间偶发 `[WARN] 回读失败`：切源时当前链路 DDC 短暂中断，命令已生效（下一条命令读到新输入源为证）；
5. 结论：**纯视频线 DDC（dxva2/MCCS）即可完成 4 路切换，无需 USB 上行线**；G-Menu 在 HDMI1 状态下能控制，同样走视频线 DDC（其自带 `DDCHelperLib.dll` 即 dxva2 物理监视器 API 实现）；
6. 最终交付：`ddc/new-ddc.exe`（正式版：`1|2|3|4` 静默发送 0x11/0x12/0x0F/0x10；无参数按当前源自动切换；`debug1-4` / `debuglist` / `help` 调试与说明），功能版 `ddc/ddc.exe` 保留诊断命令。

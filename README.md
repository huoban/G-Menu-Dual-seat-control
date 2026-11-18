# G-Menu 逆向研究与 AOC 输入源切换工具集

本项目围绕 **AOC AGON AG275UXM２** 显示器（G-Menu 识别为 `AOCA526`，型号代号 `AG275`）展开。我们在逆向分析官方 G-Menu 软件的基础上，整理出完整的 DDC／CI 输入源协议，并实现了三套互不依赖的切换工具。最终方案已在 **ASTER 双席位**（席位１：`administrator`，席位２：`huoban`，共享同一台主机与同一台显示器）真机验证通过：两个席位均可一键切换 HDMI１／HDMI２／DP１／DP２，且无需连接显示器 USB 上行线。

> 合规声明：所有自研代码仅对使用者自有设备发送标准 DDC／CI（VCP `0x60`）命令，用于输入源互操作；不修改固件、不绕过授权、不涉及破解。逆向资料仅用于理解协议与排查故障。

---

## 一、项目定位与核心结论

| 项目 | 说明 |
|------|------|
| 目标设备 | AOC AGON AG275UXM２，`MONITOR\AOCA526` |
| 输入源编码 | `DP1=0x0F`、`DP2=0x10`、`HDMI1=0x11`、`HDMI2=0x12`（来源：反编译枚举 `StandardSource_E`） |
| 控制协议 | DDC／CI，VCP `0x60`（Input Source）；软件帧 `6E 51 84 03 60 <Hi> <Lo> <XOR>` |
| 关键发现 | G-Menu 并非只有一条 DDC 通路，其原生库 `DDCHelperLib.dll` 内含 dxva2、NVAPI、AMD ADL、Intel 直连、MtxApi 等多个后端 |
| 最终方案 | `aoc-switch.exe`：按「DDCHelperLib → NVAPI → dxva2」顺序自动择路，两个席位通用 |
| 验证状态 | 席位２从 HDMI２ 切至 DP１，写入返回 `0x00000000`，回读确认 `0x0F`；席位１同时可读 `0x0F` |

**为什么原工具在席位２失败：** `new-ddc.exe` 只走 Windows MCCS／dxva2，而该接口只枚举当前会话的物理监视器。席位２的画面经「Intel 核显 → 绿联 HDMI 一进二出分配器 → AOC HDMI２」，分配器不透传 DDC／CI 写事务，故报 `Win32 0x1F`。G-Menu 之所以仍能切换，是因为它调用 `DDCHelperLib.dll`，由该库改走另一条可用后端（例如经 RTX ５０７０ Ti 的 DP１ 链路），与分配器无关。

---

## 二、目录结构与职责

| 路径 | 职责 | 是否提交 |
|------|------|----------|
| `AocInputSwitcher/` | 早期的 USB 上行线通道工具（RhHidAPI／MI_02 桥），含协议文档与诊断脚本；现作为历史路线保留 | 源码与文档提交 |
| `ddc/` | 纯 DDC／CI 工具集，含 `ddc.exe`、`new-ddc.exe`、`aoc-switch.exe` 三套实现及构建脚本 | 源码、脚本、文档提交 |
| `probe/` | 只读通道诊断探针，逐索引读取 VCP `0x60` 对比 DDCHelperLib 与 dxva2 | 源码提交 |
| `decompiled/` | 六个 .NET 程序集的反编译 C# 源码，用于协议取证 | 不提交（第三方衍生作品） |
| `GMeun/` | G-Menu 原版完整安装包（Electron 壳、运行库、原生 DLL），逆向对象 | 不提交（第三方商业软件，约３４７ ＭＢ） |
| `.gitnexus/` | GitNexus 本地代码图谱缓存与启动器 | 不提交（缓存，目录内自带忽略规则） |
| `.claude/`、`.trae/` | AI 编码助手技能与项目规则配置 | 可选提交（纯文本） |
| `AI开发功能说明.md` | 编译迭代流水账，共十余个版本记录 | 提交 |
| `G-Menu_反汇编总结.md` | REA ＋ Ghidra 反汇编证据、导出表、协议推导 | 提交 |
| `AGENTS.md`、`CLAUDE.md` | GitNexus 编码约束（编辑前impact、提交前detect-changes） | 提交 |
| `icon.ico` | 程序图标资源（第三方） | 不提交 |
| `USBViewAll.txt`、`7z2604-x64.exe` | 硬件枚举日志与第三方压缩工具 | 不提交 |

根目录的 `.gitignore` 已按上表配置，Git 提交时只会包含自研源码与文档。

---

## 三、自研代码梳理

| 源文件 | 核心类与入口 | 通道 | 命令／用途 |
|--------|--------------|------|------------|
| `ddc/aoc-switch.cs` | `DdcHelper`（动态加载原生库）、`NvApi`、`Dxva2`、`Program.Main` | 三通道自动择路 | `1｜2｜3｜4` 静默切换、无参自动切换、`diag`、`list`、`debug1..4`；最终交付版本 `v3.4` |
| `ddc/new-ddc.cs` | `NativeMethods`、`MonitorEntry`、`Program.Main` | 仅 dxva2／MCCS | 静默切换 `1｜2｜3｜4`、无参自动切换、`debuglist`；席位１可用 |
| `ddc/ddc.cs` | `NativeMethods`、`MonitorEntry`、`Program.Main` | 仅 dxva2／MCCS | `test`、`list`、`query`、`diag`、`watch`、直接切换；带完整诊断输出 |
| `AocInputSwitcher/Program.cs` | `HidRawChannel`、`Monitor`、`Program.Main` | USB HID（MI_02 桥／RhHidAPI） | `hdmi1｜hdmi2｜dp1｜dp2｜query｜diag｜test｜list`；需显示器 USB 上行线 |
| `probe/DdcChannelProbe.cs` | `DdcHelper`、`Dxva2`、`Program.Main` | DDCHelperLib 对比 dxva2 | 只读诊断，不执行任何切换；用于定位通道差异 |

### ３.１　最终版 `aoc-switch.exe` 的实现要点

１. **库加载**：只从 exe 运行目录读取 `DDCHelperLib.dll`，缺失时显示全角提示并等待回车；不写任何缓存文件。
２. **初始化顺序**：必须先 `DDCCIHelperIni()`，再 `EnumDisplayIDIni()` 完成显示器枚举，随后 `getStandardDDCCIValue_C()` 才可读。缺少枚举步骤是早期版本「diag 可读、切换不可读」的根因。
３. **返回值语义**：函数返回 `0` 即成功；`DdcValue.Result` 为读到的当前值，`DdcValue.Value` 为 VCP 最大值。早期版本误判为「`Result==0` 才成功」，导致有效读数被丢弃。
４. **窗口行为**：编译为 `/target:winexe`，静默切换无控制台窗口；仅在缺库、`diag`、`list`、`debug*` 与 `help` 时按需附加或创建控制台。
５. **退出码**：`0` 成功、`1` 参数错误、`2` 未找到库／显示器、`3` 切换失败。

### ３.２　构建

```
cd ddc
build.bat
```

`build.bat` 使用系统自带 `csc.exe`，分别编译 `ddc.exe`、`new-ddc.exe`（AnyCPU）与 `aoc-switch.exe`（x86，`/win32icon:icon.ico`）。若目录内没有 `icon.ico`，脚本自动跳过图标嵌入，不影响功能。

---

## 四、反编译资料索引

| 反编译文件 | 对应程序集 | 主要价值 |
|------------|------------|----------|
| `decompiled/G_Menu/` | `G_Menu.dll` | Electron 后端服务层、`SetSource(string)` 入口 |
| `decompiled/Zeasn.Framework.Core.Lib/` | `Zeasn.Framework.Core.Lib.dll` | `DisPlayBase.SetSource` 门面层调用链 |
| `decompiled/Zeasn.Monitor.Lib/` | `Zeasn.Monitor.Lib.dll` | `HubRTK.SetDDC`、`ScalerType` 分派、DDC 帧构造 |
| `decompiled/Zeasn.DDC.Lib/` | `Zeasn.DDC.Lib.dll` | P／Invoke `DDCHelperLib.dll` 的结构体定义与字段语义 |
| `decompiled/Zeasn.Equipment.Entity.Lib/` | `Zeasn.Equipment.Entity.Lib.dll` | `StandardSource_E` 输入源枚举、`OP_60_InputSource` |
| `decompiled/Zeasn.USB.ENE.Lib/` | `Zeasn.USB.ENE.Lib.dll` | ENE 芯片 USB 路径，配套原生 `EneEc.dll` |

原生库 `DDCHelperLib.dll` 的导入表证据：

- `dxva2.dll`：`SetVCPFeature`、`GetVCPFeatureAndVCPFeatureReply`、`GetPhysicalMonitorsFromHMONITOR`；
- `nvapi.dll`／`nvapi64.dll`：`nvapi_QueryInterface`，对应 NVIDIA I２C 通路；
- `atiadlxx.dll`、`atiddc.dll`：`ADL_Display_WriteAndReadI2C`，对应 AMD 通路；
- Intel 直连：`OpenIntelCard`、`i_InitSDVOBus`、`sdvo_ddc_i2c_put_byte`；
- 其它：`MtxApi.dll`、可选设备 `\\.\MDDCDRV`。

完整推导过程见 `G-Menu_反汇编总结.md`；逐版本改动见 `AI开发功能说明.md`。

---

## 五、快速使用

部署时把以下两个文件放在同一目录：

```
aoc-switch.exe
DDCHelperLib.dll        :: 从 G-Menu 安装目录 resources\bin\ 复制
```

常用命令：

```
aoc-switch.exe 3          :: 静默切换到 DP1（席位1 画面）
aoc-switch.exe 2          :: 静默切换到 HDMI2（席位2 画面）
aoc-switch.exe            :: 静默自动切换（0x12 与 0x0F 互切）
aoc-switch.exe debug3     :: 带过程输出切换到 DP1
aoc-switch.exe diag       :: 三通道体检（只读）
aoc-switch.exe help       :: 帮助
```

`DDCHelperLib.dll` 缺失时，程序会显示：

```
当前目录中无 DDCHelperLib.dll，请复制到当前目录。
下载地址http://list.wgpro.com/code/aoc
```

---

## 六、第三方组件与提交边界

下列资源版权归原厂所有，**不随本仓库分发**，需使用者自行从 G-Menu 安装包获取：

- `DDCHelperLib.dll`、`RhHidAPI.dll`、`EneEc.dll` 及 `Zeasn.*.dll`；
- `GMeun/` 整个安装目录；
- `decompiled/` 反编译源码；
- `icon.ico`、`USBViewAll.txt`、`7z2604-x64.exe` 等杂项资源；
- 所有编译产物（`*.exe`、`*.dll`）与硬件枚举日志。

仓库仅保留自研源码、构建脚本、说明文档与逆向分析记录。这样既避免体积与许可风险，也保证拿到源码后可复现构建。

---

## 七、文档导航

- `G-Menu_反汇编总结.md`：逆向方法论、原生库导出表、协议帧逐字节推导。
- `AI开发功能说明.md`：从 ０.２.０ 到最终交付版本的完整迭代记录。
- `ddc/README.md`：纯 DDC／CI 工具的协议、限制与真机验证记录。
- `AocInputSwitcher/README.md`、`协议说明.md`：USB HID 通道的历史方案与协议细节。

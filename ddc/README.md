# ddc.exe —— AOC AG275UXM2 输入源切换（纯 DDC/CI，经 DP1 的 DDC 信号通路）

> 从 G-Menu 反汇编成果中“分离”出的**仅 DDC 控制**控制台工具：
> 只发标准 DDC/CI MCCS 命令（VCP 0x60 输入源），经 Windows 显示栈沿 **DP1 接口的 DDC 信号通路**
> 切换显示器输入源 **HDMI1 / HDMI2 / DP1 / DP2**。
>
> 不依赖显示器 USB 上行线，不加载 RhHidAPI / EneEc 等厂商 DLL，不运行 G-Menu。

---

## 1. 工作原理（为什么这就是“DP1 的 DDC 信号通路”）

Windows 提供标准的 **MCCS / 物理监视器 API**（`lowlevelmonitorconfigurationapi.h`，实现在系统自带
`dxva2.dll`），用于经显示连接链路对显示器发送 DDC/CI 命令。链路如下：

```
ddc.exe
  │  SetVCPFeature / GetVCPFeatureAndVCPFeatureReply（dxva2.dll）
  ▼
Windows 显示驱动（monitor.sys + 显卡 miniport）
  │  DP AUX → I2C（DDC/CI 总线，从地址 0x6E / 寄存器 0x51）
  ▼
显示器 DDC 总线 ──► 显示器 MCU（执行 VCP 0x60 “Input Source”）
```

* DDC/CI 帧（`6E 51 84 03 60 00 xx CHK` 等）由显示驱动按 MCCS 规范封装；
  `ddc.exe` 只指定 **VCP 代码 0x60 与 16 位数值**，因此“全程走 DDC 信号通路”，且不接触任何厂商私有通道。
* 显示器当前连接在 **DP1** 时，DDC 就沿 DP1 链路的 DDC/AUX 线传输——这就是本工具的目标场景。

> **与 G-Menu 的区别**：G-Menu 是通过显示器 **USB 上行线**上的 Realtek HID 桥（MI_02）或 ENE WinUSB
> 私有接口访问显示器内部 I2C，需要厂商 DLL 与对应驱动；`ddc.exe` 走的是 **Windows 显示栈的 DDC**
> （即显卡 → 视频线 → 显示器），零依赖、零安装。两者发送的 VCP 语义与数值完全一致（详见 §2）。

---

## 2. 逆向依据（G-Menu 反汇编证据）

| 证据 | 位置 | 内容 |
|---|---|---|
| VCP 0x60 = 输入源 | `decompiled/Zeasn.Equipment.Entity.Lib/Zeasn.Equipment.Entity.Lib.decompiled.cs:10679` | `OP_60_InputSource`（描述“PIPPB信号源”） |
| 输入源编码 | 同上 `:11063` `StandardSource_E` | **DP1=15(0x0F)、DP2=16(0x10)、HDMI1=17(0x11)、HDMI2=18(0x12)**；USB_C1=21、USB_C2=22 |
| DDC/CI 帧格式 | [`../G-Menu_反汇编总结.md`](../G-Menu_反汇编总结.md) §4 | Set：`6E 51 84 03 60 00 <code> <XOR>`；Get：`6E 51 82 01 60 <XOR>`；XOR 校验 |
| 调用链 | `decompiled/Zeasn.Framework.Core.Lib/...:1186`、`decompiled/G_Menu/...:98248` | `SetSource(string)` → `DisPlayBase.SetSource` → VCP 0x60 |

`ddc.exe test` 会把上述 5 条帧（DP1/DP2/HDMI1/HDMI2 + 查询）逐字节重算并与 G-Menu 反编译结果比对，
**无需连接显示器**即可验证协议逻辑。

> 帧格式仅用于离线核对：运行时的 `SetVCPFeature(hPhys, 0x60, value)` 由显示驱动生成同义帧，
> 工具本身不直接拼发原始字节流。

---

## 3. 命令用法

```
ddc.exe dp1      [--monitor N]     # 切换到 DP1
ddc.exe dp2      [--monitor N]     # 切换到 DP2
ddc.exe hdmi1    [--monitor N]     # 切换到 HDMI1
ddc.exe hdmi2    [--monitor N]     # 切换到 HDMI2
ddc.exe query    [--monitor N]     # 查询当前输入源（VCP 0x60 读）
ddc.exe list                       # 列出显示器 / 物理监视器 / 型号
ddc.exe diag     [--monitor N]     # DDC 诊断：MCCS 能力位 + VCP 读取
ddc.exe watch    [--monitor N]     # 持续监视输入源（0.5s 轮询，Ctrl+C 退出）
ddc.exe test                       # 离线自检（无需显示器）
ddc.exe help                       # 帮助
```

选项：

| 选项 | 说明 |
|---|---|
| `--monitor N` / `-m N` | 指定第 N 个显示器（从 1 开始，顺序与 `list` 一致）。默认自动选择型号含 `AOC` / `AGON` / `AG27` 的显示器 |

**退出码**：`0`=成功 `1`=用法错误 `2`=未找到/无法选定显示器 `3`=DDC 读写失败 `4`=自检失败

### new-ddc.exe（正式版：参数 / 无参数自动 / debug / help）

```
new-ddc.exe 1|2|3|4     :: 直接发送切换信号（静默，无输出，执行完退出）
                         1=HDMI1(0x11)  2=HDMI2(0x12)  3=DP1(0x0F)  4=DP2(0x10)
new-ddc.exe             :: 自动切换（静默）：
                         0x12(HDMI2) → 0x0F(DP1)；0x0F(DP1) → 0x12(HDMI2)；
                         0x11(HDMI1) / 0x10(DP2) / 其它 → 0x0F(DP1)
new-ddc.exe debug1|debug2|debug3|debug4   :: 带调试输出执行对应切换
new-ddc.exe debuglist   :: 列出显示器信息
new-ddc.exe help        :: 显示完整规则
```

- 静默模式（参数 1-4、无参数）**不输出任何信息**，仅以退出码反馈：
  `0`=成功 `1`=参数错误 `2`=未找到显示器 `3`=DDC 写入失败；
- 调试模式输出与 `ddc.exe` 一致（候选显示器、当前输入源、发送与回读确认结果）；
- 多显示器时自动优先 AOC 并逐个尝试；`new_ddc.exe` 为同内容别名（兼容旧文件名）。

---

## 4. 编译

源码为单文件 `ddc.cs`（C# 5 语法，兼容 Windows 自带 .NET Framework 4.x 的 `csc.exe`，AnyCPU）。

```
build.bat
:: 或
powershell -NoProfile -ExecutionPolicy Bypass -File build.ps1
```

等价命令：

```
C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe /nologo /target:exe /platform:anycpu ^
    /optimize+ /codepage:65001 /out:ddc.exe ddc.cs
```

产物：`ddc.exe`（单文件；运行依赖系统自带 `dxva2.dll` / `user32.dll`，无第三方依赖）。

---

## 5. 使用前提与限制

1. 显示器与 PC 通过 **DP1** 连接（或其它支持 MCCS 的视频口）；本工具最终以 Windows 能识别的
   物理监视器为准。
2. 显示器 OSD 中 **DDC/CI 开关必须为“开”**（AOC：设置 → DDC/CI → 开）。若关闭，所有 DDC 命令
   都会失败（常见 Win32 31 / 无应答）。
3. **切走当前输入后，本输入口的 DDC 会暂时失效**：显示器已工作在其它输入上，DP1 链路的 DDC
   通常不再应答，`ddc.exe` 后续命令会失败——属正常现象。要切回 DP1，需用显示器按键，或从
   HDMI1/HDMI2 侧设备再执行（部分固件在非活动输入仍应答 DDC，可先试）。
4. 部分显示器固件把 VCP 0x60 设为只读（不接受主机改写）：此时 `SetVCPFeature` 报错或写入后
   不动作，只能使用显示器按键；或改用厂商通道工具（`../AocInputSwitcher`，需要 USB 上行线）。
5. 多显示器时先用 `list` 确认编号，再用 `--monitor N` 指定，避免切错设备。
6. 本工具仅运行于 Windows；虚拟显示器 / 远程桌面 / 部分 KVM 场景没有 MCCS 能力，`list` 会显示
   “物理监视器: 不可用”。
7. **切换命令发出后，工具会自动回读 VCP 0x60 确认**（约 5 秒）：
   - `[OK] 显示器已确认切换到 XXX` = 显示器确实切过去了；
   - `[WARN] …回读仍为 0x??` = 命令已 ACK，但显示器固件没有应用输入源切换；
   - 若出现第 2 种情况，说明该固件不允许主机侧 DDC 直接改输入源（不同批次固件策略不同），
     可改用显示器按键或 G-Menu 的 USB 桥通道。
8. **多显示器（含双显卡）**：工具会按“AOC 优先”的顺序遍历 Windows 枚举到的**所有**显示器，
   逐个尝试读写并回读确认；可用 `--monitor N` 强制指定某一条显示链路（DP1 独显 / HDMI2 核显可能各算一个）。

---

## 6. 真机试跑建议（按顺序）

```
:: 1) 离线自检（任何机器可跑，5 项应全部 PASS）
ddc.exe test

:: 2) 确认显示器被识别为 AOC
ddc.exe list

:: 3) 查看当前输入源（连接在 DP1 时应读到 0x0F / DP1）
ddc.exe query

:: 4) DDC 诊断（MCCS 能力位、VCP 读取；MCCS 版本错误可忽略）
ddc.exe diag

:: 5) 实际切换（显示器会切到目标输入；目标输入需有信号）
::    注意: 命令发出后工具会回读确认，输出 [OK] 已确认 / [WARN] 未确认
ddc.exe hdmi2

:: 6) 观察切换过程（另开一个窗口运行）：
ddc.exe watch
```

---

## 7. 排错

| 现象 | 可能原因 / 处理 |
|---|---|
| `未检测到任何显示器` | 显示器未开/未连；或 Windows 显示设置里看不到它（驱动/接口问题） |
| `物理监视器: 不可用` | 虚拟显示器/远程桌面；或显卡未启用 MCCS/DDC 直通 |
| `VCP 0x60 读取失败 (Win32 31)` | 显示器 OSD 的 DDC/CI 未开；或显示器当前输入与 PC 输入不一致；或该固件禁用 DDC |
| `DDC 写入失败 (Win32 31)` | 同上；另可尝试管理员身份运行；确认视频线（DP1）接触良好 |
| 写入成功但显示器未切换 | 该固件忽略主机侧 VCP 0x60 写入（少数机型）；只能用按键或厂商 USB 通道工具 |
| 切换后再次执行失败 | 正常：显示器已离开当前输入，DDC 暂时失效；切回后恢复 |
| 多个显示器切错对象 | `list` 查看编号后加 `--monitor N` |
| 结果与 G-Menu 不同步 | `query` 读取的是显示器当前实际值；G-Menu 若同时运行，二者互不冲突（通道不同） |

Win32 错误码对照（退出码 3 时工具会打印十六进制码）：

| 码 | 含义 | 处理 |
|---|---|---|
| `0xC0262589` | DDC/CI 消息命令字段无效 | 瞬时错误，工具会自动重试；持续出现则查 OSD DDC/CI 开关 |
| `0xC0262584` | 显示器不支持该 VCP 代码 | 输入切换过程中偶发；持续出现说明固件不支持主机改输入源 |
| `0xC02625DC` | MCCS 版本不匹配（`GetMonitorCapabilities`） | **可忽略**：不影响 VCP 读写，仅表示 EDID/驱动未声明 MCCS 版本 |
| `0xC0262585`~`0xC026258A` | DDC/CI 通信类错误（校验/长度/数据） | 检查视频线、降低切换频率、重试 |
| `31` / `0x0000001F` | 设备无应答 | 显示器 OSD 的 DDC/CI 未开，或当前输入与 PC 输入不一致 |

---

## 8. 文件清单

| 文件 | 说明 |
|---|---|
| `ddc.cs` | 唯一源码（控制台程序，单文件，无第三方依赖） |
| `build.bat` / `build.ps1` | 一键编译脚本（调用系统自带 csc.exe） |
| `README.md` | 本文档 |

---

## 9. 合规说明

* 仅对**用户自有设备**发送标准 DDC/CI（MCCS）命令，用于切换输入源等互操作操作；
* 不修改显示器固件、不绕过任何授权机制、不涉及破解；
* 协议依据来自用户安装包内组件的反汇编分析（见 `../G-Menu_反汇编总结.md`）。

---

## 10. 真机验证结论（2026-10-18，AG275UXM2 / DP1）

- 环境：AOC AG275UXM2；PC 经 **DP1（NV 独显）** 与 **HDMI2（Intel 核显）** 连接；显示器 USB 上行线未连接。
- `ddc.exe list`：识别 1 个显示器（`MONITOR\AOCA526`），自动选中。
- `ddc.exe dp1`（从 HDMI2）→ 回读 0x0F，确认切换到 DP1。
- `ddc.exe dp2`（从 DP1）→ 回读 0x10，确认切换到 DP2（DP2 无设备也可切，与 G-Menu 行为一致）。
- `ddc.exe hdmi1` / `ddc.exe hdmi2` → 多次验证成功（回读 0x11 / 0x12）。
- 切换瞬间偶发 `[WARN] 回读失败`：显示器切源时当前链路 DDC 短暂中断，**命令已生效**（下一条命令即可读到新输入源）。属正常现象。
- 结论：**无需 USB 上行线，纯视频线 DDC 即可完成 HDMI1/HDMI2/DP1/DP2 切换**；G-Menu 能在 HDMI1 状态下控制，也是同一视频线 DDC 通道（其自带 `DDCHelperLib.dll` 即 dxva2 实现）。

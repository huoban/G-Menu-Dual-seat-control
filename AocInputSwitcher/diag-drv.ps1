Write-Host '==== [1/4] 读取 VID_05F6/PID_AC2F 全部节点驱动真实属性 ===='
Write-Host '       （重点看 DriverProvider / Service / UpperFilters / Interfaces）'
$m = Get-PnpDevice -Present -ErrorAction SilentlyContinue | Where-Object { $_.InstanceId -like '*VID_05F6*' }
if (-not $m) {
    Write-Host '未找到目标设备节点（USB 上行线未连接？）'
} else {
    $m | ForEach-Object {
        Write-Host ('===== ' + $_.InstanceId + ' =====')
        Get-PnpDeviceProperty -InstanceId $_.InstanceId -KeyName 'DEVPKEY_Device_DriverProvider','DEVPKEY_Device_DriverVersion','DEVPKEY_Device_DriverDesc','DEVPKEY_Device_Service','DEVPKEY_Device_InfPath','DEVPKEY_Device_UpperFilters','DEVPKEY_Device_LowerFilters','DEVPKEY_Device_Interfaces' -ErrorAction SilentlyContinue | Format-List
    }
}
Write-Host ''
Write-Host '==== [2/4] USBDevice（WinUsb）类下安装的驱动包 ===='
pnputil /enum-drivers /class "USBDevice"
Write-Host ''
Write-Host '==== [3/4] winusb.sys 驱动状态与签名 ===='
driverquery /v | Select-String -Pattern 'winusb'
Write-Host ''
Write-Host '==== [4/4] Realtek / G-Menu 残留驱动与服务扫描 ===='
driverquery /v | Select-String -Pattern 'gmenu|agon|aoc|realtek|rtspusb|rtk'
Write-Host ''
Write-Host '==== 诊断完成。请把本窗口整段输出复制反馈 ===='
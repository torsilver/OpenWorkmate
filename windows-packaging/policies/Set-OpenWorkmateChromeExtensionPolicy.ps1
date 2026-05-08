#Requires -Version 5.1
<#
.SYNOPSIS
  为当前用户（HKCU）注册 Chrome ExtensionInstallForcelist，指向本机 HTTPS 上的 OpenWorkmate 扩展更新清单。

.PARAMETER ExtensionId
  Chrome 扩展 ID（32 位 a-p，与 CRX 私钥一致）。

.PARAMETER UpdatesXmlUrl
  完整 HTTPS URL，例如 https://localhost:3000/OpenWorkmate-chrome/updates.xml

.PARAMETER MachineScope
  若指定则写入 HKLM（需管理员提升），否则写入 HKCU。
#>
param(
  [Parameter(Mandatory = $true)]
  [string]$ExtensionId,

  [Parameter(Mandatory = $true)]
  [string]$UpdatesXmlUrl,

  [switch]$MachineScope
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$entry = "$ExtensionId;$UpdatesXmlUrl"
$policyPath = if ($MachineScope) {
  'HKLM:\Software\Policies\Google\Chrome'
} else {
  'HKCU:\Software\Policies\Google\Chrome'
}

if (-not (Test-Path $policyPath)) {
  New-Item -Path $policyPath -Force | Out-Null
}

$name = 'ExtensionInstallForcelist'
$existing = @()
try {
  $prop = Get-ItemProperty -Path $policyPath -Name $name -ErrorAction SilentlyContinue
  if ($null -ne $prop -and $null -ne $prop.$name) {
    $raw = $prop.$name
    if ($raw -is [string[]]) {
      $existing = @($raw | Where-Object { $_ -and $_.Trim().Length -gt 0 })
    } elseif ($raw -is [string] -and $raw.Trim().Length -gt 0) {
      $existing = @($raw.Trim())
    }
  }
} catch { }

$merged = New-Object System.Collections.Generic.List[string]
$seen = @{}
foreach ($e in $existing) {
  if (-not $seen.ContainsKey($e)) {
    $merged.Add($e)
    $seen[$e] = $true
  }
}
if (-not $seen.ContainsKey($entry)) {
  $merged.Add($entry)
}

Set-ItemProperty -Path $policyPath -Name $name -Value ($merged.ToArray()) -Type MultiString
Write-Host "已写入 $policyPath\$name ：$entry" -ForegroundColor Green
Write-Host "请完全退出并重新打开 Chrome，或在地址栏打开 chrome://policy 点「重新加载政策」。" -ForegroundColor Cyan

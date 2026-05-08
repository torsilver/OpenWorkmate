#Requires -Version 5.1
<#
.SYNOPSIS
  从 ExtensionInstallForcelist 中移除 Taskly 条目（仅删除包含指定 ExtensionId 的项，不影响其它扩展）。
#>
param(
  [Parameter(Mandatory = $true)]
  [string]$ExtensionId,

  [switch]$MachineScope
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$policyPath = if ($MachineScope) {
  'HKLM:\Software\Policies\Google\Chrome'
} else {
  'HKCU:\Software\Policies\Google\Chrome'
}

if (-not (Test-Path $policyPath)) {
  Write-Host "策略路径不存在，无需移除。" -ForegroundColor Yellow
  return
}

$name = 'ExtensionInstallForcelist'
$prop = Get-ItemProperty -Path $policyPath -Name $name -ErrorAction SilentlyContinue
if ($null -eq $prop -or $null -eq $prop.$name) {
  Write-Host "无 ExtensionInstallForcelist，无需移除。" -ForegroundColor Yellow
  return
}

$raw = $prop.$name
$list = @()
if ($raw -is [string[]]) {
  $list = @($raw | Where-Object { $_ -and $_.Trim().Length -gt 0 })
} elseif ($raw -is [string]) {
  $list = @($raw.Trim())
}

$prefix = "$ExtensionId;"
$filtered = $list | Where-Object { -not $_.StartsWith($prefix, [System.StringComparison]::Ordinal) }

if ($filtered.Count -eq $list.Count) {
  Write-Host "未找到 Taskly 对应条目。" -ForegroundColor Yellow
  return
}

if ($filtered.Count -eq 0) {
  Remove-ItemProperty -Path $policyPath -Name $name -ErrorAction SilentlyContinue
  Write-Host "已删除 ExtensionInstallForcelist（仅剩 Taskly 时已清空该值）。" -ForegroundColor Green
} else {
  Set-ItemProperty -Path $policyPath -Name $name -Value $filtered -Type MultiString
  Write-Host "已从 ExtensionInstallForcelist 移除 Taskly。" -ForegroundColor Green
}

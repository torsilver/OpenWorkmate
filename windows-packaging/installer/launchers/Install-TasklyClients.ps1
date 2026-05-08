#Requires -Version 5.1
<#
  MSI 结束页异步调用：按勾选尝试注册 Chrome 策略、Office 旁加载、WPS 指引。
  对齐 Chrome：policies/Set-TasklyChromeExtensionPolicy.ps1（HKCU ExtensionInstallForcelist）
#>
param(
  [Parameter(Mandatory = $true)]
  [string]$InstallRoot,

  [string]$Chrome = '',
  [string]$Office = '',
  [string]$Wps = ''
)

$ErrorActionPreference = 'Continue'
$logPath = Join-Path $InstallRoot 'Install-TasklyClients.log'

function Write-Log([string]$message) {
  $line = '{0} {1}' -f (Get-Date -Format 'o'), $message
  Add-Content -LiteralPath $logPath -Value $line -Encoding UTF8
}

function Read-StaticHostPort {
  $pack = Join-Path $InstallRoot 'Taskly.StaticHost\appsettings.Pack.json'
  if (-not (Test-Path $pack)) { return 3000 }
  try {
    $j = Get-Content -LiteralPath $pack -Raw -Encoding UTF8 | ConvertFrom-Json
    if ($null -ne $j.StaticHost.Port) {
      return [int]$j.StaticHost.Port
    }
  } catch { }
  return 3000
}

function Get-OfficeManifestId([string]$manifestPath) {
  $raw = Get-Content -LiteralPath $manifestPath -Raw -Encoding UTF8
  if ($raw -match '<Id>([^<]+)</Id>') {
    return $Matches[1].Trim()
  }
  throw "无法在 manifest 中解析 Id：$manifestPath"
}

function Format-OfficeRegistryId([string]$id) {
  $g = [guid]::Parse($id)
  return $g.ToString('B').ToUpperInvariant()
}

Write-Log "Start Install-TasklyClients.ps1 Chrome=$Chrome Office=$Office Wps=$Wps"

$port = Read-StaticHostPort
$baseUrl = "https://localhost:$port"
$updatesUrl = "$baseUrl/taskly-chrome/updates.xml"

# --- Chrome ---
if ($Chrome -eq '1') {
  $pf = [Environment]::GetFolderPath('ProgramFiles')
  $pf86 = [Environment]::GetFolderPath('ProgramFilesX86')
  $chromeExe = @(
    (Join-Path $pf 'Google\Chrome\Application\chrome.exe'),
    (Join-Path $pf86 'Google\Chrome\Application\chrome.exe')
  ) | Where-Object { Test-Path $_ } | Select-Object -First 1

  if (-not $chromeExe) {
    Write-Log 'Chrome: 未检测到 Google Chrome 可执行文件路径（仍将写入策略，供日后安装 Chrome 后生效）。'
  } else {
    Write-Log "Chrome: 检测到 $chromeExe"
  }

  $idFile = Join-Path $InstallRoot 'taskly-chrome\extension-id.txt'
  if (-not (Test-Path $idFile)) {
    Write-Log 'Chrome: 未找到 taskly-chrome\extension-id.txt（打包时是否跳过 CRX？），已跳过。'
  } else {
    $eid = (Get-Content -LiteralPath $idFile -Raw).Trim()
    $setPol = Join-Path $InstallRoot 'policies\Set-TasklyChromeExtensionPolicy.ps1'
    if (-not (Test-Path $setPol)) {
      Write-Log "Chrome: 缺少 $setPol"
    } else {
      try {
        & $setPol -ExtensionId $eid -UpdatesXmlUrl $updatesUrl
        Write-Log 'Chrome: 已写入当前用户的 ExtensionInstallForcelist（请完全重启 Chrome 并查看 chrome://policy）。'
      } catch {
        Write-Log "Chrome: 策略写入失败: $($_.Exception.Message)"
      }
    }
  }
}

# --- Office ---
if ($Office -eq '1') {
  $manifestPath = Join-Path $InstallRoot 'office-addin\manifest-install.xml'
  if (-not (Test-Path $manifestPath)) {
    Write-Log "Office: 缺少 manifest $manifestPath"
  } else {
    $wordReg = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\WINWORD.EXE'
    $excelReg = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\EXCEL.EXE'
    $hasOffice = (Test-Path $wordReg) -or (Test-Path $excelReg)
    if (-not $hasOffice) {
      Write-Log 'Office: 未在 App Paths 检测到 WINWORD/EXCEL（仍尝试写入当前用户 WEF 注册表）。'
    }
    try {
      $mid = Get-OfficeManifestId $manifestPath
      $regId = Format-OfficeRegistryId $mid
      foreach ($ver in @('16.0', '15.0')) {
        $key = "HKCU:\Software\Microsoft\Office\$ver\WEF\Developer\$regId"
        try {
          New-Item -Path $key -Force | Out-Null
          New-ItemProperty -Path $key -Name 'ManifestPath' -PropertyType String -Value $manifestPath -Force | Out-Null
          Write-Log "Office: 已写入 $key -> ManifestPath"
        } catch {
          Write-Log "Office: $ver 写入失败: $($_.Exception.Message)"
        }
      }
    } catch {
      Write-Log "Office: $($_.Exception.Message)"
    }
  }
}

# --- WPS ---
if ($Wps -eq '1') {
  $pf = [Environment]::GetFolderPath('ProgramFiles')
  $pf86 = [Environment]::GetFolderPath('ProgramFilesX86')
  $wpsExe = @(
    (Join-Path $pf 'Kingsoft\WPS Office\ksolaunch.exe'),
    (Join-Path $pf86 'Kingsoft\WPS Office\ksolaunch.exe'),
    (Join-Path $pf 'WPS Office\ksolaunch.exe')
  ) | Where-Object { Test-Path $_ } | Select-Object -First 1

  if (-not $wpsExe) {
    Write-Log 'WPS: 未检测到常见 ksolaunch 路径；仍写入本地指引文件。'
  } else {
    Write-Log "WPS: 检测到 $wpsExe"
  }

  $wpsRoot = Join-Path $InstallRoot 'wps-addin'
  $hintFile = Join-Path $InstallRoot 'wps-addon-auto-hint.txt'
  @(
    'WPS add-in cannot be fully silent like Chrome policy; open WPS load-addin / dev tools and point to the folder below.',
    "构建产物路径: $wpsRoot",
    "HTTPS 静态资源（若已 Start Taskly）: $baseUrl/wps/",
    '仓库约定开发调试使用 wpsjs debug；安装版请参阅 Taskly-ClientSetup.html。'
  ) | Set-Content -LiteralPath $hintFile -Encoding UTF8
  Write-Log "WPS: 已写入 $hintFile"
}

Write-Log 'Done.'

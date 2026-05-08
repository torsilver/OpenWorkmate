#Requires -Version 5.1
<#
.SYNOPSIS
  一键将 OpenWorkmate 整项目打入 windows-packaging/stage，并默认编译 Windows 安装包（MSI）。

.PARAMETER BuildInstaller
  默认 `$true`：在 stage 就绪后调用 WiX 编译 `installer/OpenWorkmate.wxs`，输出 `dist/OpenWorkmate.msi`。传 `-BuildInstaller:$false` 可跳过 MSI，仅保留 stage。

.PARAMETER FrameworkDependent
  若指定则 dotnet publish 使用 --self-contained false（体积小，目标机需安装 .NET 运行时）。默认打自包含包。

.PARAMETER BackendTargetFramework
  AI 后台（OpenWorkmate.Server）目标框架：net10.0-windows（托盘、单实例、Office COM/Interop，默认，适合 MSI/桌面）或 net10.0（无上述 Windows 专用代码，适合纯控制台/无托盘场景）。StaticHost 仍为单一 TFM（用户安装包不含 ai-gateway）。
#>
param(
  [string]$RepoRoot = "",
  [bool]$BuildInstaller = $true,
  [switch]$FrameworkDependent,
  [switch]$SkipChrome,
  [ValidateSet('net10.0-windows', 'net10.0')]
  [string]$BackendTargetFramework = 'net10.0-windows',
  [string]$Runtime = "win-x64",
  [string]$Configuration = "Release",
  [int]$StaticHostPort = 3000
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$here = Split-Path -Parent $MyInvocation.MyCommand.Path
if (-not $RepoRoot) {
  $RepoRoot = Resolve-Path (Join-Path $here '..\..')
}
$RepoRoot = (Resolve-Path $RepoRoot).Path

$packagingRoot = Join-Path $RepoRoot 'windows-packaging'
$stageRoot = Join-Path $packagingRoot 'stage'
$backendProj = Join-Path $RepoRoot 'backend\OpenWorkmate.Server.csproj'
$staticHostProj = Join-Path $packagingRoot 'runtime\OpenWorkmate.StaticHost\OpenWorkmate.StaticHost.csproj'
$officeSrc = Join-Path $RepoRoot 'office-addin'
$wpsRoot = Join-Path $RepoRoot 'wps-addin-new'

function Invoke-Step {
  param([string]$Message, [scriptblock]$Action)
  Write-Host ""
  Write-Host "=== $Message ===" -ForegroundColor Cyan
  & $Action
  if ($LASTEXITCODE -ne 0 -and $null -ne $LASTEXITCODE) {
    throw "Step failed: $Message (exit $LASTEXITCODE)"
  }
}

if (Test-Path $stageRoot) {
  Remove-Item -Path $stageRoot -Recurse -Force
}
New-Item -ItemType Directory -Force -Path $stageRoot | Out-Null

$sc = -not $FrameworkDependent.IsPresent
$scArg = if ($sc) { @('--self-contained', 'true') } else { @('--self-contained', 'false') }

Invoke-Step "dotnet publish backend ($BackendTargetFramework)" {
  $out = Join-Path $stageRoot 'OpenWorkmate.Server'
  dotnet publish $backendProj -c $Configuration -f $BackendTargetFramework -r $Runtime @scArg -o $out --nologo
}

Invoke-Step "dotnet publish OpenWorkmate.StaticHost" {
  $out = Join-Path $stageRoot 'OpenWorkmate.StaticHost'
  dotnet publish $staticHostProj -c $Configuration -r $Runtime @scArg -o $out --nologo
}

Invoke-Step "Copy Office add-in static files" {
  $dest = Join-Path $stageRoot 'office-addin'
  New-Item -ItemType Directory -Force -Path $dest | Out-Null
  $robolog = Join-Path $env:TEMP 'robocopy-office.log'
  & robocopy.exe $officeSrc $dest /E /XD node_modules /NFL /NDL /NJH /NJS /nc /ns /np | Out-Null
  if ($LASTEXITCODE -ge 8) { throw "robocopy office-addin failed: $LASTEXITCODE" }
  $global:LASTEXITCODE = 0
}

Invoke-Step "Build WPS add-in (vite build)" {
  Push-Location $wpsRoot
  try {
    if (-not (Test-Path 'node_modules')) {
      npm install --no-fund --no-audit | Out-Host
    }
    npm run build | Out-Host
    if ($LASTEXITCODE -ne 0) { throw 'wps npm run build failed' }
  } finally {
    Pop-Location
  }
  $dist = Join-Path $wpsRoot 'dist'
  if (-not (Test-Path $dist)) { throw "wps dist not found: $dist" }
  $dest = Join-Path $stageRoot 'wps-addin'
  New-Item -ItemType Directory -Force -Path $dest | Out-Null
  & robocopy.exe $dist $dest /E /NFL /NDL /NJH /NJS /nc /ns /np | Out-Null
  if ($LASTEXITCODE -ge 8) { throw "robocopy wps dist failed" }
  $global:LASTEXITCODE = 0
}

if (-not $SkipChrome) {
  & (Join-Path $here 'Build-ChromeCrx.ps1') -RepoRoot $RepoRoot -HttpsPort $StaticHostPort
} else {
  Write-Host "=== Skip Chrome CRX (-SkipChrome) ===" -ForegroundColor Yellow
  New-Item -ItemType Directory -Force -Path (Join-Path $stageRoot 'OpenWorkmate-chrome') | Out-Null
}

Invoke-Step "Generate Office manifest + StaticHost appsettings.Pack.json" {
  $baseUrl = "https://localhost:$StaticHostPort"
  $tpl = Get-Content (Join-Path $packagingRoot 'templates\office-manifest-install.xml') -Raw -Encoding UTF8
  $manifestOut = Join-Path $stageRoot 'office-addin\manifest-install.xml'
  $tpl.Replace('{{BASE_URL}}', $baseUrl) | Set-Content -Path $manifestOut -Encoding UTF8

  $policiesSrc = Join-Path $packagingRoot 'policies'
  $policiesDest = Join-Path $stageRoot 'policies'
  if (Test-Path $policiesSrc) {
    Copy-Item -Path $policiesSrc -Destination $policiesDest -Recurse -Force
  }

  $staticDir = Join-Path $stageRoot 'OpenWorkmate.StaticHost'
  $appSettings = @{
    StaticHost = @{
      Port       = "$StaticHostPort"
      OfficeRoot = "..\\office-addin"
      WpsRoot    = "..\\wps-addin"
      ChromeRoot = "..\\OpenWorkmate-chrome"
    }
  } | ConvertTo-Json -Depth 5
  Set-Content -Path (Join-Path $staticDir 'appsettings.Pack.json') -Value $appSettings -Encoding UTF8

  $idPath = Join-Path $stageRoot 'OpenWorkmate-chrome\extension-id.txt'
  if (Test-Path $idPath) {
    $eid = (Get-Content $idPath -Raw).Trim()
    $setPol = Join-Path $stageRoot 'policies\Set-OpenWorkmateChromeExtensionPolicy.ps1'
    $remPol = Join-Path $stageRoot 'policies\Remove-OpenWorkmateChromeExtensionPolicy.ps1'
    $updUrl = "https://localhost:$StaticHostPort/OpenWorkmate-chrome/updates.xml"
    $sb = [System.Text.StringBuilder]::new()
    [void]$sb.AppendLine('Chrome policy (ExtensionInstallForcelist): run one of the following, then restart Chrome and check chrome://policy .')
    [void]$sb.AppendLine('')
    [void]$sb.AppendLine(('powershell -NoProfile -ExecutionPolicy Bypass -File "{0}" -ExtensionId {1} -UpdatesXmlUrl "{2}"' -f $setPol, $eid, $updUrl))
    [void]$sb.AppendLine('')
    [void]$sb.AppendLine('Remove OpenWorkmate policy entry only:')
    [void]$sb.AppendLine(('powershell -NoProfile -ExecutionPolicy Bypass -File "{0}" -ExtensionId {1}' -f $remPol, $eid))
    Set-Content -Path (Join-Path $stageRoot 'Chrome-policy-hint.txt') -Value $sb.ToString() -Encoding UTF8

    $installChrome = @"
# Apply Chrome ExtensionInstallForcelist for this install (run after Start OpenWorkmate).
`$ErrorActionPreference = 'Stop'
`$root = Split-Path -Parent `$MyInvocation.MyCommand.Path
`$eid = (Get-Content (Join-Path `$root 'OpenWorkmate-chrome\extension-id.txt') -Raw).Trim()
`$set = Join-Path `$root 'policies\Set-OpenWorkmateChromeExtensionPolicy.ps1'
& `$set -ExtensionId `$eid -UpdatesXmlUrl 'https://localhost:$StaticHostPort/OpenWorkmate-chrome/updates.xml'
Write-Host 'OK. Fully quit Chrome and reopen, then open chrome://policy to verify.' -ForegroundColor Green
"@
    Set-Content -Path (Join-Path $stageRoot 'Install-ChromePolicy.ps1') -Value $installChrome -Encoding UTF8
  }

  $clientSetupSrc = Join-Path $packagingRoot 'templates\OpenWorkmate-ClientSetup.html'
  if (Test-Path $clientSetupSrc) {
    Copy-Item -Path $clientSetupSrc -Destination (Join-Path $stageRoot 'OpenWorkmate-ClientSetup.html') -Force
  }

  $installClientsSrc = Join-Path $packagingRoot 'installer\launchers\Install-OpenWorkmateClients.ps1'
  if (Test-Path $installClientsSrc) {
    Copy-Item -Path $installClientsSrc -Destination (Join-Path $stageRoot 'Install-OpenWorkmateClients.ps1') -Force
  }
}

if ($BackendTargetFramework -eq 'net10.0-windows') {
  $startPs1 = @"
# Generated by Pack-OpenWorkmate.ps1: start services without flashing console windows
`$ErrorActionPreference = 'Stop'
`$root = Split-Path -Parent `$MyInvocation.MyCommand.Path
Set-Location `$root
`$be = Join-Path `$root 'OpenWorkmate.Server\OpenWorkmate.Server.exe'
`$hs = Join-Path `$root 'OpenWorkmate.StaticHost\OpenWorkmate.StaticHost.exe'
`$wdBe = Join-Path `$root 'OpenWorkmate.Server'
`$wdHs = Join-Path `$root 'OpenWorkmate.StaticHost'
# Backend: net10.0-windows (tray + single-instance + Office interop)
Start-Process -WindowStyle Hidden -FilePath `$be -WorkingDirectory `$wdBe -ArgumentList '--tray'
Start-Sleep -Seconds 1
Start-Process -WindowStyle Hidden -FilePath `$hs -WorkingDirectory `$wdHs
"@
}
else {
  $startPs1 = @"
# Generated by Pack-OpenWorkmate.ps1: start services without flashing console windows
`$ErrorActionPreference = 'Stop'
`$root = Split-Path -Parent `$MyInvocation.MyCommand.Path
Set-Location `$root
`$be = Join-Path `$root 'OpenWorkmate.Server\OpenWorkmate.Server.exe'
`$hs = Join-Path `$root 'OpenWorkmate.StaticHost\OpenWorkmate.StaticHost.exe'
`$wdBe = Join-Path `$root 'OpenWorkmate.Server'
`$wdHs = Join-Path `$root 'OpenWorkmate.StaticHost'
# Backend: net10.0 (no tray / no Office COM interop in this build)
Start-Process -WindowStyle Hidden -FilePath `$be -WorkingDirectory `$wdBe
Start-Sleep -Seconds 1
Start-Process -WindowStyle Hidden -FilePath `$hs -WorkingDirectory `$wdHs
"@
}
Set-Content -Path (Join-Path $stageRoot 'Start-OpenWorkmate.ps1') -Value $startPs1 -Encoding UTF8

Write-Host ""
Write-Host "Pack complete. Stage: $stageRoot" -ForegroundColor Green
Write-Host "Run locally:  powershell -NoProfile -ExecutionPolicy Bypass -File `"$stageRoot\Start-OpenWorkmate.ps1`"" -ForegroundColor Cyan
Write-Host "Office sideload manifest: $stageRoot\office-addin\manifest-install.xml" -ForegroundColor Cyan

if ($BuildInstaller) {
  $wixCmd = Get-Command wix -ErrorAction SilentlyContinue
  if (-not $wixCmd) {
    throw "WiX CLI not found. Install: dotnet tool install --global wix`nThen re-open the terminal, or skip MSI with: -BuildInstaller:`$false"
  }
  $wxsMain = Join-Path $packagingRoot 'installer\OpenWorkmate.wxs'
  $wxsUiFork = Join-Path $packagingRoot 'installer\WixUI_OpenWorkmate_InstallDir.wxs'
  $wxsDialogs = Join-Path $packagingRoot 'installer\OpenWorkmate-Dialogs.wxs'
  $distDir = Join-Path $packagingRoot 'dist'
  New-Item -ItemType Directory -Force -Path $distDir | Out-Null
  $msiOut = Join-Path $distDir 'OpenWorkmate.msi'
  $launcherDir = Join-Path $packagingRoot 'installer\launchers'
  & wix build $wxsMain $wxsUiFork $wxsDialogs `
    -ext WixToolset.UI.wixext `
    -bindpath "stage=$stageRoot" `
    -bindpath "launcher=$launcherDir" `
    -arch x64 `
    -out $msiOut
  if ($LASTEXITCODE -ne 0) { throw "WiX build failed (wix build)." }
  Write-Host "MSI output: $msiOut" -ForegroundColor Green
}

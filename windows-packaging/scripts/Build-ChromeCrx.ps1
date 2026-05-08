#Requires -Version 5.1
<#
.SYNOPSIS
  将 chrome-extension 打成 CRX，并生成 Chrome 外部更新用的 updates.xml（供 Taskly.StaticHost 在 /taskly-chrome 下托管）。

.PARAMETER RepoRoot
  仓库根目录（含 chrome-extension）。默认：本脚本上溯两级（windows-packaging/scripts -> 仓库根）。

.PARAMETER SkipIfNoKey
  若私钥不存在则跳过（不失败），用于仅构建后端/加载项时。
#>
param(
  [string]$RepoRoot = "",
  [int]$HttpsPort = 3000,
  [string]$KeyFileName = "taskly-chrome-extension.pem",
  [switch]$SkipIfNoKey
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$here = Split-Path -Parent $MyInvocation.MyCommand.Path
if (-not $RepoRoot) {
  $RepoRoot = Resolve-Path (Join-Path $here '..\..')
}
$RepoRoot = (Resolve-Path $RepoRoot).Path

$packagingRoot = Join-Path $RepoRoot 'windows-packaging'
$keysDir = Join-Path $packagingRoot 'keys'
$keyPath = Join-Path $keysDir $KeyFileName
$extDir = Join-Path $RepoRoot 'chrome-extension'
$stageChrome = Join-Path $packagingRoot 'stage\taskly-chrome'
$nodeTools = Join-Path $packagingRoot 'scripts\node-tools'
$templatePath = Join-Path $packagingRoot 'chrome-update\updates.xml.template'

if (-not (Test-Path $extDir)) {
  throw "Extension directory not found: $extDir"
}

if (-not (Test-Path $keyPath)) {
  if ($SkipIfNoKey) {
    Write-Warning "Skip Chrome CRX: no private key. Place PEM at $keyPath or run openssl genrsa."
    return
  }
  New-Item -ItemType Directory -Force -Path $keysDir | Out-Null
  $openssl = Get-Command openssl -ErrorAction SilentlyContinue
  if (-not $openssl) {
    throw "Missing private key and openssl not in PATH. Place PEM at $keyPath or install OpenSSL."
  }
  Write-Host "Generating new private key: $keyPath" -ForegroundColor Cyan
  & openssl genrsa -out $keyPath 2048
}

if (-not (Test-Path (Join-Path $nodeTools 'node_modules\crx'))) {
  Write-Host "Installing node-tools (first time)..." -ForegroundColor Cyan
  Push-Location $nodeTools
  try {
    npm install --no-fund --no-audit | Out-Host
  } finally {
    Pop-Location
  }
}

New-Item -ItemType Directory -Force -Path $stageChrome | Out-Null
$outCrx = Join-Path $stageChrome 'Taskly.crx'
$idPath = Join-Path $stageChrome 'extension-id.txt'

& node (Join-Path $nodeTools 'pack-crx.cjs') $extDir $keyPath $outCrx $idPath
if ($LASTEXITCODE -ne 0) { throw "pack-crx.cjs failed" }

$extensionId = (Get-Content -Path $idPath -Raw).Trim()
$manifest = Get-Content (Join-Path $extDir 'manifest.json') -Raw | ConvertFrom-Json
$version = [string]$manifest.version
if (-not $version) { throw 'manifest.json missing version' }

$crxUrl = "https://localhost:$HttpsPort/taskly-chrome/Taskly.crx"
$template = Get-Content -Path $templatePath -Raw -Encoding UTF8
$xml = $template.Replace('{{EXTENSION_ID}}', $extensionId).Replace('{{CRX_URL}}', $crxUrl).Replace('{{VERSION}}', $version)
$outXml = Join-Path $stageChrome 'updates.xml'
Set-Content -Path $outXml -Value $xml -Encoding UTF8

Write-Host "Wrote: $outCrx" -ForegroundColor Green
Write-Host "Wrote: $outXml (ExtensionId=$extensionId, version=$version)" -ForegroundColor Green

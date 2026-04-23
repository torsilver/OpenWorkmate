#Requires -Version 5.1
<#
.SYNOPSIS
  启动 Office 加载项本地 HTTPS 静态服务（默认 https://localhost:3000），便于在 Word 中旁加载 manifest.xml。

.DESCRIPTION
  - 首次运行前会在 office-addin 目录执行 npm install（需已安装 Node.js）。
  - 使用 Microsoft office-addin-dev-certs 生成本机受信任的开发证书。
  - 优先配合 Word：插入 → 加载项 → 上传我的加载项 → 选择 manifest.xml。

.PARAMETER Port
  HTTPS 端口，默认 3000（须与 manifest.xml 中 URL 一致；若改端口请同步修改 manifest）。
#>
param(
  [int]$Port = 3000
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$here = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location $here

if (-not (Get-Command npm -ErrorAction SilentlyContinue)) {
  Write-Error "未找到 npm。请先安装 Node.js LTS：https://nodejs.org/"
}

if (-not (Test-Path (Join-Path $here 'node_modules\office-addin-dev-certs'))) {
  Write-Host "正在安装 office-addin 开发依赖（首次）..." -ForegroundColor Cyan
  npm install --no-fund --no-audit
}

$env:PORT = "$Port"
Write-Host ""
Write-Host "启动 HTTPS 静态服务（Word 旁加载详见下方 Node 输出）..." -ForegroundColor Green
npm run dev

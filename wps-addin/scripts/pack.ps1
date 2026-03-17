# Office Copilot WPS 加载项打包脚本
# 将任务窗格源码（index.html、taskpane.css、taskpane.js）同步到可安装目录 office-copilot-wps_1.0.0，
# 便于在更新插件后重新打包，无需手工复制。
# 用法：在 wps-addin 目录下执行 .\scripts\pack.ps1 或在仓库根目录执行 .\wps-addin\scripts\pack.ps1

$ErrorActionPreference = "Stop"
$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$WpsAddinRoot = Split-Path -Parent $ScriptDir
$OutDir = Join-Path $WpsAddinRoot "office-copilot-wps_1.0.0"

if (-not (Test-Path $OutDir)) {
    Write-Error "OutDir not found: $OutDir"
}

$srcIndex = Join-Path $WpsAddinRoot "index.html"
$srcCss = Join-Path $WpsAddinRoot "taskpane.css"
$srcJs = Join-Path $WpsAddinRoot "taskpane.js"

foreach ($f in @($srcIndex, $srcCss, $srcJs)) {
    if (-not (Test-Path $f)) {
        Write-Error "Source file not found: $f"
    }
}

# 任务窗格页面：源码 index.html 作为 taskpane.html 内容
Copy-Item -Path $srcIndex -Destination (Join-Path $OutDir "taskpane.html") -Force
Copy-Item -Path $srcCss -Destination (Join-Path $OutDir "taskpane.css") -Force
Copy-Item -Path $srcJs -Destination (Join-Path $OutDir "taskpane.js") -Force

Write-Host "Synced to: $OutDir"
Write-Host "Files: taskpane.html, taskpane.css, taskpane.js"
Write-Host "Next: copy folder office-copilot-wps_1.0.0 to jsaddons to install or update."

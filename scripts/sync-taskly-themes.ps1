# 将 shared-ui 下的对话主题文件同步到 Chrome 扩展、WPS public、Office add-in。
$root = Split-Path -Parent $PSScriptRoot
$src = Join-Path $root "shared-ui"
$targets = @(
    (Join-Path $root "chrome-extension"),
    (Join-Path $root "wps-addin-new\public"),
    (Join-Path $root "office-addin")
)
foreach ($f in @("chat-themes.css", "taskly-theme-boot.js")) {
    $from = Join-Path $src $f
    if (-not (Test-Path $from)) { Write-Error "Missing $from"; exit 1 }
    foreach ($t in $targets) {
        Copy-Item -LiteralPath $from -Destination (Join-Path $t $f) -Force
    }
}
Write-Host "Synced chat-themes.css and taskly-theme-boot.js to chrome-extension, wps-addin-new/public, office-addin."

@echo off
chcp 65001 >nul
setlocal EnableExtensions
set "ROOT=%~dp0"
cd /d "%ROOT%"

echo.
echo [Taskly] 启动 AI 后台 (http://localhost:8765)、AI Gateway (http://127.0.0.1:8777)、Office 加载项 HTTPS (https://localhost:3000)
echo Gateway：策略 GET /api/policy/aggregated、LLM 转发 POST /llm/v1/chat/completions、观测落盘 data/sessions/*.jsonl
echo AI 后台（调试）：目标框架 net10.0（不含 net10.0-windows 专用代码路径）。
echo Office：taskpane/manifest 由 office-addin\Start-OfficeAddinDev.ps1 托管（需 Node.js/npm）。
echo WPS：wpsjs debug 已写在脚本末尾但默认注释；需要时在 office-addin 区块下方取消 REM。
echo.

netstat -ano | findstr ":8765" | findstr /I "LISTENING" >nul 2>&1
if not errorlevel 1 (
  echo [警告] 本机已有进程在监听 8765，再次启动会报 address already in use。
  echo        请先关掉已运行的 AI 后台窗口，或在管理员 CMD 中查 PID 后结束进程：
  echo          netstat -ano ^| findstr ":8765"
  echo          taskkill /PID 上一步最后一列数字 /F
  echo.
)

netstat -ano | findstr ":3000" | findstr /I "LISTENING" >nul 2>&1
if not errorlevel 1 (
  echo [警告] 本机已有进程在监听 3000，Office 加载项 HTTPS 将无法绑定。
  echo        请先结束占用 3000 的进程，或改 office-addin 端口并同步 manifest.xml。
  echo.
)

start "Taskly AI Backend :8765" cmd /k cd /d "%ROOT%backend" ^&^& dotnet run --framework net10.0 --launch-profile OfficeCopilot
start "Taskly AI Gateway :8777" cmd /k cd /d "%ROOT%ai-gateway" ^&^& dotnet run --launch-profile http

start "Taskly Office Add-in HTTPS :3000" powershell.exe -NoExit -ExecutionPolicy Bypass -File "%ROOT%office-addin\Start-OfficeAddinDev.ps1"

REM ---------------------------------------------------------------------------
REM WPS 加载项调试（默认关闭）：需在 wps-addin-new 目录执行，依赖本机已安装 WPS 与 npm。
REM 启用：去掉下一行开头的 REM（会多开一个控制台跑 npx wpsjs debug）。
REM 文档：docs\WPS插件调试指南.md
REM ---------------------------------------------------------------------------
start "Taskly WPS Add-in (wpsjs debug)" cmd /k cd /d "%ROOT%wps-addin-new" ^&^& npx wpsjs debug

echo.
echo 已打开：AI 后台、AI Gateway、Office 加载项 HTTPS 共三个控制台窗口（WPS 行默认注释）。
echo 关闭本窗口不会停止上述后台。
echo.
pause
endlocal

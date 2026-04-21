@echo off
chcp 65001 >nul
setlocal EnableExtensions
set "ROOT=%~dp0"
cd /d "%ROOT%"

echo.
echo [Taskly] 启动 AI 后台 (http://localhost:8765) 与 AI Gateway (http://127.0.0.1:8777)
echo Gateway：策略 GET /api/policy/aggregated、LLM 转发 POST /llm/v1/chat/completions、观测落盘 data/sessions/*.jsonl
echo AI 后台（调试）：目标框架 net10.0（不含 net10.0-windows 专用代码路径）。
echo.

netstat -ano | findstr ":8765" | findstr /I "LISTENING" >nul 2>&1
if not errorlevel 1 (
  echo [警告] 本机已有进程在监听 8765，再次启动会报 address already in use。
  echo        请先关掉已运行的 AI 后台窗口，或在管理员 CMD 中查 PID 后结束进程：
  echo          netstat -ano ^| findstr ":8765"
  echo          taskkill /PID 上一步最后一列数字 /F
  echo.
)

start "Taskly AI Backend :8765" cmd /k cd /d "%ROOT%backend" ^&^& dotnet run --framework net10.0 --launch-profile OfficeCopilot
start "Taskly AI Gateway :8777" cmd /k cd /d "%ROOT%ai-gateway" ^&^& dotnet run --launch-profile http

echo.
echo 已打开两个控制台窗口。关闭本窗口不会停止上述后台。
echo.
pause
endlocal

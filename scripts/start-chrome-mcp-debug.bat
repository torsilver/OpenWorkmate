@echo off
setlocal EnableExtensions
set "SCRIPT_DIR=%~dp0"
set "PROFILE=%SCRIPT_DIR%chrome-mcp-debug-profile"
if not exist "%PROFILE%" mkdir "%PROFILE%"

set "CHROME="
if exist "C:\Program Files\Google\Chrome\Application\chrome.exe" (
  set "CHROME=C:\Program Files\Google\Chrome\Application\chrome.exe"
) else if exist "C:\Program Files (x86)\Google\Chrome\Application\chrome.exe" (
  set "CHROME=C:\Program Files (x86)\Google\Chrome\Application\chrome.exe"
) else (
  echo [start-chrome-mcp-debug] 未找到 Chrome，请安装或编辑本 BAT 中的路径。
  pause
  exit /b 1
)

echo.
echo 远程调试端口: 9222
echo 用户数据目录: %PROFILE%
echo Cursor MCP 配置: --browser-url=http://127.0.0.1:9222
echo.
start "" "%CHROME%" --remote-debugging-port=9222 --user-data-dir="%PROFILE%"
endlocal

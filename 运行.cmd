@echo off
chcp 65001 >nul
cd /d "%~dp0"

echo ============================================
echo   正在编译并启动 ImeTip ...
echo   关闭悬浮窗（右键点它）即可退出程序
echo ============================================
echo.

dotnet run

echo.
echo 程序已退出。
pause >nul

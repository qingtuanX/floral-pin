@echo off
setlocal
title 花笺钉桌面
cd /d "%~dp0"

if not exist "%~dp0FloralPin.exe" (
  echo.
  echo [错误] 当前目录找不到 FloralPin.exe。
  echo 请先双击 build.cmd 重新编译，或确认文件是否被杀毒软件删除。
  echo.
  pause
  exit /b 1
)

:menu
cls
echo ================================================
echo   花笺（floral-notepaper）钉到桌面 小工具
echo ================================================
echo.
echo   用法：鼠标指到便签上，按热键即可在
echo   「钉到桌面 / 恢复置顶」之间切换，每个便签单独控制。
echo   默认热键 Ctrl+Alt+D，被占用时自动改用 Ctrl+Alt+P 或 Ctrl+Alt+K。
echo.
echo   [1] 开始后台守护（让热键生效）
echo   [2] 切换鼠标下的便签（3 秒内移过去，自动切换）
echo   [3] 全部钉到桌面
echo   [4] 全部恢复置顶
echo   [5] 查看当前状态（含实际热键、日志路径）
echo   [6] 开启开机自启
echo   [7] 关闭开机自启
echo   [0] 退出
echo.
set /p "choice=请输入数字回车: "

if "%choice%"=="1" goto start
if "%choice%"=="2" goto toggle
if "%choice%"=="3" goto pinall
if "%choice%"=="4" goto restoreall
if "%choice%"=="5" goto status
if "%choice%"=="6" goto install
if "%choice%"=="7" goto uninstall
if "%choice%"=="0" exit /b 0
goto menu

:start
echo.
start "" "%~dp0FloralPin.exe" watch
echo 守护已启动（无窗口后台运行）。
echo 实际生效的热键请在 [5] 状态里确认。
echo.
pause
goto menu

:toggle
echo.
echo 3 秒内把鼠标移到要切换的便签上...
timeout /t 3 /nobreak >nul
FloralPin.exe toggle
echo.
pause
goto menu

:pinall
echo.
FloralPin.exe pin
pause
goto menu

:restoreall
echo.
FloralPin.exe restore
pause
goto menu

:status
echo.
FloralPin.exe status
echo.
pause
goto menu

:install
echo.
FloralPin.exe install
pause
goto menu

:uninstall
echo.
FloralPin.exe uninstall
pause
goto menu

@echo off
setlocal
set "CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%CSC%" set "CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe"
if not exist "%CSC%" (
  echo [build] csc.exe not found.
  exit /b 1
)
rem winexe: no console window (safe for autostart / daemon); CLI output still
rem works when launched from a console because std handles are inherited.
"%CSC%" /nologo /target:winexe /codepage:65001 /out:"%~dp0FloralPin.exe" "%~dp0FloralPin.cs"
if errorlevel 1 (
  echo [build] FAILED
  exit /b 1
)
echo [build] OK: %~dp0FloralPin.exe

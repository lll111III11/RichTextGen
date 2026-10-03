@echo off
rem ============================================================
rem  Build native (C++) installer, ZERO dependency.
rem  Output: RichTextGen-Setup.exe (Win32, no .NET, no WebView2
rem  runtime, static CRT so no vcruntime*.dll needed either).
rem
rem  NOTE: keep this file ASCII-only. cmd.exe reads .bat in the
rem        OEM codepage, so UTF-8 Chinese literals would break.
rem ============================================================
setlocal
set ROOT=%~dp0
set VS=C:\Program Files (x86)\Microsoft Visual Studio\2019\Community\Common7\Tools\VsDevCmd.bat

echo [1/3] generate embedded payload ...
powershell -NoProfile -ExecutionPolicy Bypass -File "%ROOT%gen_embedded.ps1" "%ROOT%payload_web" "%ROOT%embedded.h"
if errorlevel 1 goto fail

echo [2/3] setup MSVC environment ...
call "%VS%" -arch=x86 -host_arch=x64 >nul
if errorlevel 1 goto fail

echo [3/3] compile Setup.cpp (x86, static CRT) ...
cl.exe /nologo /O2 /MT /EHsc /utf-8 /DUNICODE /D_UNICODE /Fe:"%ROOT%RichTextGen-Setup.exe" "%ROOT%Setup.cpp" /link /SUBSYSTEM:WINDOWS
if errorlevel 1 goto fail

echo.
echo DONE -^> %ROOT%RichTextGen-Setup.exe
exit /b 0

:fail
echo.
echo BUILD FAILED
exit /b 1

@echo off
rem ============================================================
rem  Build the standalone "full uninstall" tool (native C++,
rem  console, requiresAdministrator). No embedded payload.
rem  Output: RichTextGen-FullUninstall.exe
rem
rem  NOTE: keep this file ASCII-only. cmd.exe reads .bat in the
rem        OEM codepage, so UTF-8 Chinese literals would break.
rem ============================================================
setlocal
set ROOT=%~dp0
set VS=C:\Program Files (x86)\Microsoft Visual Studio\2019\Community\Common7\Tools\VsDevCmd.bat

echo [1/3] setup MSVC environment ...
call "%VS%" -arch=x86 -host_arch=x64 >nul
if errorlevel 1 goto fail

echo [2/3] compile FullUninstall.cpp (x86, static CRT, console) ...
cl.exe /nologo /O2 /MT /EHsc /utf-8 /DUNICODE /D_UNICODE /Fo"%ROOT%fulluninstall.obj" /Fe:"%ROOT%RichTextGen-FullUninstall.exe" "%ROOT%FullUninstall.cpp" /link /SUBSYSTEM:CONSOLE
if errorlevel 1 goto fail

echo [3/3] embed manifest (requireAdministrator) ...
mt.exe -nologo -manifest "%ROOT%fulluninstall.manifest" -outputresource:"%ROOT%RichTextGen-FullUninstall.exe";#1
if errorlevel 1 goto fail

echo.
echo DONE -^> %ROOT%RichTextGen-FullUninstall.exe
exit /b 0

:fail
echo.
echo BUILD FAILED
exit /b 1

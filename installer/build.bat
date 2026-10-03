@echo off
rem ============================================================
rem  One command build: HTML/WebView2 app  ->  native installer
rem
rem  The installer itself is native C++ (zero dependency: no
rem  .NET, no WebView2 runtime, static CRT). build_native.bat
rem  compiles Setup.cpp with MSVC + embedded payload.
rem
rem  NOTE: keep this file ASCII-only. cmd.exe reads .bat in the
rem        OEM codepage, so UTF-8 Chinese literals would break.
rem ============================================================
setlocal
set ROOT=%~dp0
set APP=%ROOT%..\WebUi\RichTextGen.Web.csproj
set OUT=%ROOT%..\WebUi\bin\Release\net48
set PAY=%ROOT%payload_web

echo [1/3] build app (Release, x86) ...
dotnet build "%APP%" -c Release -v m
if errorlevel 1 goto fail

echo [2/3] refresh installer payload ...
if not exist "%PAY%" mkdir "%PAY%"
del /q "%PAY%\*.*" 2>nul
copy /y "%OUT%\*.exe" "%PAY%\" >nul
if errorlevel 1 goto fail
copy /y "%OUT%\*.exe.config" "%PAY%\" >nul
copy /y "%OUT%\Microsoft.Web.WebView2.Core.dll" "%PAY%\" >nul
if errorlevel 1 goto fail
copy /y "%OUT%\Microsoft.Web.WebView2.WinForms.dll" "%PAY%\" >nul
if errorlevel 1 goto fail
copy /y "%OUT%\WebView2Loader.dll" "%PAY%\" >nul
if errorlevel 1 goto fail

echo [3/3] build native installer (C++, zero dependency) ...
call "%ROOT%build_native.bat"
if errorlevel 1 goto fail

echo.
echo DONE -^> %ROOT%RichTextGen-Setup.exe
exit /b 0

:fail
echo.
echo BUILD FAILED
exit /b 1

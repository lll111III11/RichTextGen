@echo off
rem ============================================================
rem  One command build:
rem    app -> signed exe -> installer payload -> signed installer
rem    + standalone signed "full uninstall" tool
rem
rem  Signing is skipped automatically when signing\rtg-codesign.pfx
rem  is missing, so a fresh clone still builds unsigned binaries.
rem
rem  NOTE: keep this file ASCII-only and CRLF. cmd.exe reads .bat in
rem        the OEM codepage, so UTF-8 Chinese literals would break.
rem ============================================================
setlocal
set ROOT=%~dp0
set APP=%ROOT%..\WebUi\RichTextGen.Web.csproj
set OUT=%ROOT%..\WebUi\bin\Release\net48
set PAY=%ROOT%payload_web
set SIGN=%ROOT%..\signing

echo [1/5] build app (Release, x86) ...
dotnet build "%APP%" -c Release -v m
if errorlevel 1 goto fail

echo [2/5] refresh installer payload ...
if exist "%PAY%" rd /s /q "%PAY%"
if not exist "%PAY%" mkdir "%PAY%"
copy /y "%OUT%\*.exe" "%PAY%\" >nul
if errorlevel 1 goto fail
copy /y "%OUT%\*.exe.config" "%PAY%\" >nul
copy /y "%OUT%\Microsoft.Web.WebView2.Core.dll" "%PAY%\" >nul
if errorlevel 1 goto fail
copy /y "%OUT%\Microsoft.Web.WebView2.WinForms.dll" "%PAY%\" >nul
if errorlevel 1 goto fail
copy /y "%OUT%\WebView2Loader.dll" "%PAY%\" >nul
if errorlevel 1 goto fail
rem The signing certificate is no longer shipped: setup never writes the cert stores.

echo [3/5] build native installer (C++, zero dependency) ...
call "%ROOT%build_native.bat"
if errorlevel 1 goto fail

echo [4/5] build full uninstall tool ...
call "%ROOT%build_fulluninstall.bat"
if errorlevel 1 goto fail

rem Code signing was retired: products are published unsigned on purpose.

echo.
echo DONE -^> %ROOT%RichTextGen-Setup.exe
echo DONE -^> %ROOT%RichTextGen-FullUninstall.exe
exit /b 0

:fail
echo.
echo BUILD FAILED
exit /b 1

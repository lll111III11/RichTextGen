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

echo [1/6] build app (Release, x86) ...
dotnet build "%APP%" -c Release -v m
if errorlevel 1 goto fail

echo [2/6] sign app exe ...
for %%F in ("%OUT%\*.exe") do powershell -NoProfile -ExecutionPolicy Bypass -File "%ROOT%sign.ps1" "%%~fF"
if errorlevel 1 goto fail

echo [3/6] refresh installer payload ...
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

echo [4/6] build native installer (C++, zero dependency) ...
call "%ROOT%build_native.bat"
if errorlevel 1 goto fail

echo [5/6] build full uninstall tool ...
call "%ROOT%build_fulluninstall.bat"
if errorlevel 1 goto fail

echo [6/6] sign installer + tool ...
powershell -NoProfile -ExecutionPolicy Bypass -File "%ROOT%sign.ps1" "%ROOT%RichTextGen-Setup.exe"
if errorlevel 1 goto fail
powershell -NoProfile -ExecutionPolicy Bypass -File "%ROOT%sign.ps1" "%ROOT%RichTextGen-FullUninstall.exe"
if errorlevel 1 goto fail

echo.
echo DONE -^> %ROOT%RichTextGen-Setup.exe
echo DONE -^> %ROOT%RichTextGen-FullUninstall.exe
exit /b 0

:fail
echo.
echo BUILD FAILED
exit /b 1

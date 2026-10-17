@echo off
rem ============================================================
rem  One command build: HTML/WebView2 app  ->  signed native installer
rem
rem  1) build app (Release, x86)
rem  2) sign app exe with the local code signing cert
rem  3) refresh installer payload (signed exe + config + dlls + .cer)
rem  4) build native installer, then sign the installer exe
rem
rem  Signing is skipped automatically when signing\rtg-codesign.pfx
rem  is missing, so a fresh clone still builds an unsigned installer.
rem
rem  NOTE: keep this file ASCII-only. cmd.exe reads .bat in the
rem        OEM codepage, so UTF-8 Chinese literals would break.
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

echo [2/5] sign app exe ...
for %%F in ("%OUT%\*.exe") do powershell -NoProfile -ExecutionPolicy Bypass -File "%ROOT%sign.ps1" "%%~fF"
if errorlevel 1 goto fail

echo [3/5] refresh installer payload ...
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
if exist "%SIGN%\rtg-codesign.cer" copy /y "%SIGN%\rtg-codesign.cer" "%PAY%\" >nul

echo [4/5] build native installer (C++, zero dependency) ...
call "%ROOT%build_native.bat"
if errorlevel 1 goto fail

echo [5/5] sign installer exe ...
powershell -NoProfile -ExecutionPolicy Bypass -File "%ROOT%sign.ps1" "%ROOT%RichTextGen-Setup.exe"
if errorlevel 1 goto fail

echo.
echo DONE -^> %ROOT%RichTextGen-Setup.exe
exit /b 0

:fail
echo.
echo BUILD FAILED
exit /b 1

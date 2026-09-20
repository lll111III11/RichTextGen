@echo off
rem ??????? v5.0.0 - ????
rem ?? VS2019 ? Roslyn csc??????????????????
setlocal
set CSC=C:\Program Files (x86)\Microsoft Visual Studio\2019\Community\MSBuild\Current\Bin\Roslyn\csc.exe
set ROOT=%~dp0
if not exist "%ROOT%bin" mkdir "%ROOT%bin"
"%CSC%" /nologo /target:winexe /platform:anycpu /optimize+ ^
  /out:"%ROOT%bin\RichTextGen.exe" ^
  /reference:System.dll /reference:System.Core.dll /reference:System.Drawing.dll /reference:System.Windows.Forms.dll ^
  "%ROOT%src\*.cs"
if errorlevel 1 (
  echo.
  echo [FAILED] ???????????????
  pause
  exit /b 1
)
echo.
echo [OK] ??? bin\RichTextGen.exe
pause
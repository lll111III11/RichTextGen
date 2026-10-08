@echo off
rem ============================================================
rem  build_apk.bat - build RichTextGen.apk (pure command line)
rem
rem  Toolchain: Java 8 (javac / keytool, in PATH)
rem             Android SDK build-tools 30.0.2 (aapt2 / zipalign / d8 / apksigner)
rem             JDK 17 (build-tools 30.x jars are class 53+, Java 8 cannot run them)
rem
rem  We call the jars directly: the official d8.bat / apksigner.bat need
rem  find_java.bat from the SDK "tools" package, which is not installed here
rem  (they would exit silently with no output).
rem
rem  IMPORTANT: this project sits under a path that contains non-ASCII (Chinese)
rem  directory names. aapt2 / javac cannot open such a path ("failed to open
rem  directory", error 2), so the build always stages the sources into an ASCII
rem  temp dir first and copies the result back.
rem
rem  NOTE: keep this file ASCII-only - cmd.exe reads .bat in the OEM codepage.
rem ============================================================
setlocal
set AD=E:\Android\android-sdk
set SRC=%~dp0
set STAGE=%TEMP%\rtg_apk_build
set JAVA17=C:\tools\jdk17\jdk-17.0.20.1+1\bin\java.exe

set AAPT2=%AD%\build-tools\30.0.2\aapt2.exe
set ZALIGN=%AD%\build-tools\30.0.2\zipalign.exe
set D8JAR=%AD%\build-tools\30.0.2\lib\d8.jar
set APKSIGNERJAR=%AD%\build-tools\30.0.2\lib\apksigner.jar
set AJAR=%AD%\platforms\android-30\android.jar

for %%f in ("%AAPT2%" "%ZALIGN%" "%D8JAR%" "%APKSIGNERJAR%" "%AJAR%" "%JAVA17%") do (
  if not exist %%f (
    echo MISSING TOOL: %%f
    exit /b 1
  )
)
if not exist "%SRC%res" (
  echo MISSING: %SRC%res
  exit /b 1
)

echo [0/7] stage sources into ASCII dir ...
if exist "%STAGE%" rmdir /s /q "%STAGE%"
mkdir "%STAGE%"
xcopy /e /i /q /y "%SRC%src"    "%STAGE%\src"    >nul
xcopy /e /i /q /y "%SRC%assets" "%STAGE%\assets" >nul
xcopy /e /i /q /y "%SRC%res"    "%STAGE%\res"    >nul
copy /y "%SRC%AndroidManifest.xml" "%STAGE%\AndroidManifest.xml" >nul
cd /d "%STAGE%"
mkdir apkout >nul 2>nul
cd /d "%STAGE%\apkout"
rem reuse the project keystore so every build keeps the SAME signature
rem (a changed key would make the new APK un-installable over an older one)
if exist "%SRC%rtg.keystore" copy /y "%SRC%rtg.keystore" "rtg.keystore" >nul

echo [1/7] aapt2 compile resources ...
if exist res.zip del res.zip
"%AAPT2%" compile --dir "%STAGE%\res" -o res.zip
if errorlevel 1 goto :fail

echo [2/7] aapt2 link manifest + assets ...
if exist base.apk del base.apk
"%AAPT2%" link -I "%AJAR%" --manifest "%STAGE%\AndroidManifest.xml" -A "%STAGE%\assets" --min-sdk-version 24 --target-sdk-version 30 -o base.apk res.zip
if errorlevel 1 goto :fail

echo [3/7] javac ...
if exist obj rmdir /s /q obj
mkdir obj
javac -source 1.8 -target 1.8 -encoding UTF-8 -bootclasspath "%AJAR%" -classpath "%AJAR%" -d obj "%STAGE%\src\MainActivity.java"
if errorlevel 1 goto :fail

echo [4/7] dex (d8) ...
if exist classes-bundle.jar del classes-bundle.jar
if exist classes.dex del classes.dex
jar cf classes-bundle.jar -C obj .
"%JAVA17%" -Xmx1024M -cp "%D8JAR%" com.android.tools.r8.D8 --release --lib "%AJAR%" --min-api 24 --output . classes-bundle.jar
if errorlevel 1 goto :fail
if not exist classes.dex goto :fail

echo [5/7] add classes.dex into apk ...
powershell -NoProfile -Command "Add-Type -AssemblyName System.IO.Compression.FileSystem; $z=[IO.Compression.ZipFile]::Open('%STAGE%\apkout\base.apk','Update'); [void][IO.Compression.ZipFileExtensions]::CreateEntryFromFile($z,'%STAGE%\apkout\classes.dex','classes.dex','Optimal'); $z.Dispose()"
if errorlevel 1 goto :fail

echo [6/7] zipalign ...
if exist aligned.apk del aligned.apk
"%ZALIGN%" -f 4 base.apk aligned.apk
if errorlevel 1 goto :fail

echo [7/7] sign (keystore kept in the project dir) ...
if not exist rtg.keystore keytool -genkeypair -keystore rtg.keystore -alias rtg -keyalg RSA -keysize 2048 -validity 10000 -storepass rtg123456 -keypass rtg123456 -dname "CN=RichTextGen, OU=Local, O=RichTextGen, C=CN"
if errorlevel 1 goto :fail
"%JAVA17%" -jar "%APKSIGNERJAR%" sign --ks rtg.keystore --ks-pass pass:rtg123456 --key-pass pass:rtg123456 --out RichTextGen.apk aligned.apk
if errorlevel 1 goto :fail
"%JAVA17%" -jar "%APKSIGNERJAR%" verify --print-certs RichTextGen.apk
if errorlevel 1 goto :fail

echo [copy back] ...
if not exist "%SRC%apkout" mkdir "%SRC%apkout"
copy /y "%STAGE%\apkout\RichTextGen.apk" "%SRC%apkout\RichTextGen.apk" >nul
if errorlevel 1 goto :fail
copy /y "%STAGE%\apkout\rtg.keystore" "%SRC%rtg.keystore" >nul

echo.
echo ================================================
echo  DONE: %SRC%apkout\RichTextGen.apk
echo ================================================
goto :eof

:fail
echo BUILD FAILED
exit /b 1

@echo off
rem Crea dist\AppSys-Setup-X.Y.Z.exe: compila AppSys.exe e lo incorpora nell'installer.
rem   build-installer.bat            -> usa la versione attuale di Version.cs
rem   build-installer.bat patch      -> incrementa 1.0.0 -> 1.0.1 (anche: minor, major)
rem   build-installer.bat 2.0.0      -> imposta una versione esplicita
setlocal
cd /d "%~dp0"

if not "%~1"=="" (
  powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0bump-version.ps1" %1 || exit /b 1
)
for /f "delims=" %%V in ('powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0bump-version.ps1" show') do set VER=%%V

call "%~dp0build.bat" || exit /b 1

set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
if not exist "%CSC%" set CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe
if not exist dist mkdir dist
rem Stesse traduzioni dell'app (lang\*.txt, tranne _template.txt)
set LANGRES=
if exist lang for %%F in (lang\*.txt) do if /i not "%%~nF"=="_template" call set LANGRES=%%LANGRES%% /resource:"%%F",AppSys.lang.%%~nF.txt

"%CSC%" /nologo /codepage:65001 /target:winexe /optimize+ /out:dist\AppSys-Setup-%VER%.exe ^
  /win32icon:appsys.ico /win32manifest:setup.manifest /resource:AppSys.exe,AppSys.payload ^
  /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Core.dll /r:Microsoft.CSharp.dll ^
  Setup.cs Version.cs Lang.cs %LANGRES%
if errorlevel 1 (
  echo Compilazione installer fallita.
  exit /b 1
)
echo Creato dist\AppSys-Setup-%VER%.exe
rem SHA-256 da scrivere nelle note della release ("SHA-256: ..."): l'aggiornamento automatico lo verifica
set SHA=
for /f "skip=1 delims=" %%H in ('certutil -hashfile "dist\AppSys-Setup-%VER%.exe" SHA256') do if not defined SHA set SHA=%%H
set SHA=%SHA: =%
echo SHA-256: %SHA%
> dist\AppSys-Setup-%VER%.sha256.txt echo SHA-256: %SHA%

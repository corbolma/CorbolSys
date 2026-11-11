@echo off
rem Compila AppSys.exe usando il compilatore C# incluso in Windows (.NET Framework 4.x)
setlocal
set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
if not exist "%CSC%" set CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe
if not exist "%CSC%" (
  echo Compilatore csc.exe non trovato.
  exit /b 1
)
cd /d "%~dp0"
if not exist appsys.ico powershell -NoProfile -ExecutionPolicy Bypass -File make-icon.ps1
rem Traduzioni: ogni lang\*.txt (tranne _template.txt) viene incorporato nell'exe
set LANGRES=
if exist lang for %%F in (lang\*.txt) do if /i not "%%~nF"=="_template" call set LANGRES=%%LANGRES%% /resource:"%%F",AppSys.lang.%%~nF.txt
"%CSC%" /nologo /codepage:65001 /target:winexe /optimize+ /out:AppSys.exe ^
  /win32icon:appsys.ico /resource:appsys.ico,AppSys.appsys.ico ^
  /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Core.dll /r:System.Web.Extensions.dll /r:System.Security.dll ^
  /r:System.IO.Compression.dll /r:System.IO.Compression.FileSystem.dll /r:System.Management.dll ^
  AppSys.cs SysMonitor.cs Analysis.cs AnalysisForm.cs SysTools.cs Ai.cs AiLocal.cs ClaudeCode.cs AiSettingsForm.cs Anon.cs Version.cs Lang.cs %LANGRES%
if errorlevel 1 (
  echo Compilazione fallita.
  exit /b 1
)
echo Creato AppSys.exe

@echo off
rem Compila CorbolSys.exe usando il compilatore C# incluso in Windows (.NET Framework 4.x)
setlocal
set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
if not exist "%CSC%" set CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe
if not exist "%CSC%" (
  echo Compilatore csc.exe non trovato.
  exit /b 1
)
rem WPF: serve solo per la Jump List (System.Windows.Shell)
for %%I in ("%CSC%") do set WPF=%%~dpIWPF
cd /d "%~dp0"
if not exist corbolsys.ico powershell -NoProfile -ExecutionPolicy Bypass -File make-icon.ps1
rem Traduzioni: ogni lang\*.txt (tranne _template.txt) viene incorporato nell'exe
set LANGRES=
if exist lang for %%F in (lang\*.txt) do if /i not "%%~nF"=="_template" call set LANGRES=%%LANGRES%% /resource:"%%F",CorbolSys.lang.%%~nF.txt
"%CSC%" /nologo /codepage:65001 /target:winexe /optimize+ /out:CorbolSys.exe ^
  /win32icon:corbolsys.ico /resource:corbolsys.ico,CorbolSys.corbolsys.ico ^
  /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Core.dll /r:System.Web.Extensions.dll /r:System.Security.dll ^
  /r:System.IO.Compression.dll /r:System.IO.Compression.FileSystem.dll /r:System.Management.dll ^
  /r:"%WPF%\PresentationFramework.dll" /r:"%WPF%\PresentationCore.dll" /r:"%WPF%\WindowsBase.dll" /r:System.Xaml.dll ^
  CorbolSys.cs SysMonitor.cs Analysis.cs AnalysisForm.cs DiskSpaceForm.cs CleanupForm.cs SysTools.cs Ai.cs AiLocal.cs ClaudeCode.cs AiSettingsForm.cs Anon.cs Version.cs Lang.cs OldApp.cs %LANGRES%
if errorlevel 1 (
  echo Compilazione fallita.
  exit /b 1
)
echo Creato CorbolSys.exe

@echo off
rem Builds SampleRateGuard.exe with the C# compiler that ships with Windows (.NET Framework 4.x).
rem No Visual Studio or SDK needed. Output: bin\SampleRateGuard.exe
setlocal
set FW=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319
set WM=%WINDIR%\System32\WinMetadata

if not exist "%FW%\csc.exe" (
  echo .NET Framework 4.x 64-bit compiler not found at %FW%\csc.exe
  echo It is part of Windows 10/11. Enable ".NET Framework 4.8 Advanced Services" in Windows Features if missing.
  exit /b 1
)

if not exist "%~dp0bin" mkdir "%~dp0bin"

"%FW%\csc.exe" /nologo /target:winexe /platform:x64 /optimize+ /warn:4 ^
  /r:"%FW%\System.Runtime.dll" ^
  /r:"%FW%\System.Runtime.WindowsRuntime.dll" ^
  /r:"%WM%\Windows.Foundation.winmd" ^
  /r:"%WM%\Windows.Data.winmd" ^
  /r:"%WM%\Windows.UI.winmd" ^
  /out:"%~dp0bin\SampleRateGuard.exe" ^
  "%~dp0src\SampleRateGuard.cs"

if errorlevel 1 (
  echo Build failed.
  exit /b 1
)
echo Built: %~dp0bin\SampleRateGuard.exe

@echo off
rem 编译 Stellaris 更新监控（.NET Framework 自带 csc，无需装任何东西）
setlocal
set "CSC=%SystemRoot%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%CSC%" set "CSC=%SystemRoot%\Microsoft.NET\Framework\v4.0.30319\csc.exe"
if not exist "%CSC%" (
  echo [!] 找不到 csc.exe（需要 .NET Framework 4.x）
  exit /b 1
)
"%CSC%" /nologo /target:winexe /codepage:65001 /optimize+ ^
  /out:"%~dp0StellarisUpdateMonitor.exe" ^
  /r:System.Windows.Forms.dll /r:System.Drawing.dll ^
  "%~dp0src\StellarisUpdateMonitor.cs"
if errorlevel 1 (
  echo [!] 编译失败
  exit /b 1
)
echo [ok] 已生成 StellarisUpdateMonitor.exe
endlocal

@echo off
rem 安装到 %LOCALAPPDATA% 并加入开机自启（HKCU\...\Run）
"%~dp0StellarisUpdateMonitor.exe" --install

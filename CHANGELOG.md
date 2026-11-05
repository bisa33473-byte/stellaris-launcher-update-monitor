# Changelog

## 1.0.0 — 2026-11-05

首个公开版本。

- 常驻监控 Paradox 启动器（`Paradox Launcher.exe`），启动器打开时自动显示小窗，关闭后自动隐藏
- 显示更新包大小、已下载、百分比、进度条、实时速度、剩余时间
- 支持下载完成后的补丁阶段（`正在应用补丁 N / M`）与完成状态
- 游戏 DLC / 内容下载也会显示
- 过滤元数据文件（`repository.json` / `manifest` / `.asc`）和补丁阶段的单文件进度
- 速度由采样字节差计算，不依赖日志里的 `Download speed`
- 支持开机自启（`--install` / `--uninstall`），写入 `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`
- 首次运行显示 8 秒提示，说明已在后台监视
- 单文件 24 KB，无第三方依赖，可用 .NET Framework 自带 `csc` 编译

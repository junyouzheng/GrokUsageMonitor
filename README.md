# GrokUsageMonitor

Windows 托盘程序，用来查看 SuperGrok 周额度（Chat / Imagine / Build 共用额度池）。

登录信息读取自本机 `~/.grok/auth.json`（先运行 `grok login`）。程序不会把令牌写回该文件。

## 下载

到 [Releases](https://github.com/junyouzheng/GrokUsageMonitor/releases) 下载 `GrokUsageMonitor.exe`，双击运行。自包含单文件，不需要单独安装 .NET。

## 功能

- 不占用任务栏，只显示系统托盘图标
- 窗口固定在托盘图标上方，不能拖动、不能改大小
- 左键点托盘图标：一次打开，再点一次隐藏
- 托盘右键「退出」结束程序
- 自动刷新可选 10 分钟 / 30 分钟，也可点「刷新」或按 F5
- 托盘图标显示剩余百分比
- 用量跨过 50% / 80% / 90% / 100% 时弹出提醒

命令行一次性查询：

```bat
GrokUsageMonitor.exe --once
```

## 重新编译

需要 .NET 8 SDK。

```bat
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -o dist
```

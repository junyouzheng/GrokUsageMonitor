# GrokUsageMonitor

Windows 托盘程序，用来查看 SuperGrok 周额度（Chat / Imagine / Build 共用额度池）。

登录信息读取自本机 `~/.grok/auth.json`（先运行 `grok login`）。程序不会把令牌写回该文件。

## 下载

到 [Releases](https://github.com/junyouzheng/GrokUsageMonitor/releases) 下载 `GrokUsageMonitor.exe`，双击运行。自包含单文件，不需要单独安装 .NET。

## 功能

- 不占用任务栏，只显示系统托盘图标
- 窗口固定在工作区右下角，贴边，竖版 400×640，与币讯同高，不能拖动、不能改大小
- 左键点托盘图标：一次打开，再点一次隐藏
- 托盘右键「退出」结束程序
- 启动时不拉取用量，直接显示上次刷新的结果。点「刷新」会立刻更新
- 运行中每 30 分钟核对一次。已用跨过 5%、10%、15% … 100% 时，托盘和窗口更新一次。同一档不重复；一次跨过多档，只更新到当前最高档。新周期开始时更新一次
- 托盘图标显示剩余百分比。从未刷新、且本周期尚未到 5% 时，图标为空闲状态
- 鼠标移到近 7 天走势上时，显示该点的已用百分比和当时的时间

命令行一次性查询：

```bat
GrokUsageMonitor.exe --once
```

## 重新编译

需要 .NET 8 SDK。

```bat
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -o dist
```

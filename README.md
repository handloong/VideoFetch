# VideoFetch — C# WPF Video Downloader

这是 Porn Fetch (Python/Qt 版本) 的 C# WPF 改写版，核心功能简化版。

## 功能

- 支持网站：**PornHub**、**XVideos**、**XNXX**
- 粘贴 URL → 自动获取视频信息（标题、作者、时长、缩略图）
- 选择画质后加入下载队列
- 多线程 HLS/M3U8 分段下载（默认 8 线程）
- 支持直链 MP4 下载
- 下载队列管理（并行控制、取消、清除已完成）
- 设置：下载路径、代理、并发数
- 下载历史（SQLite）
- 暗色主题 WPF 界面

## 运行要求

- Windows 10/11
- .NET 8.0 运行时（[下载](https://dotnet.microsoft.com/download/dotnet/8.0)）

## 编译方法

```bash
# 需要 .NET 8 SDK
cd VideoFetch
dotnet restore
dotnet build -c Release
dotnet run
```

或用 Visual Studio 2022 直接打开 `VideoFetch.sln`。

## 项目结构

```
VideoFetch/
├── Models/
│   └── Models.cs              # 数据模型 (VideoInfo, DownloadItem, AppSettings...)
├── Services/
│   ├── ISiteParser.cs          # 网站解析器接口
│   ├── HttpClientFactory.cs    # HTTP 客户端工厂（支持代理）
│   ├── VideoInfoService.cs     # URL 路由 -> 对应 Parser
│   ├── DownloadEngine.cs       # HLS + 直链下载引擎
│   ├── DownloadQueueService.cs # 下载队列管理
│   ├── SettingsService.cs      # 配置持久化 + SQLite 历史
│   └── Sites/
│       ├── PornHubParser.cs    # PornHub 解析
│       ├── XVideosParser.cs    # XVideos 解析
│       └── XNxxParser.cs       # XNXX 解析
├── ViewModels/
│   └── MainViewModel.cs        # 主窗口 ViewModel (MVVM)
├── Views/
│   ├── MainWindow.xaml         # 主窗口 UI
│   └── MainWindow.xaml.cs
├── Converters/
│   └── Converters.cs           # WPF 值转换器
├── Themes/
│   └── DarkTheme.xaml          # 暗色主题样式
└── App.xaml
```

## 添加新网站

1. 在 `Services/Sites/` 新建 `MySiteParser.cs`，实现 `ISiteParser` 接口
2. 在 `VideoInfoService.cs` 的 `_parsers` 列表中注册
3. 在 `Models/Models.cs` 的 `SiteType` 枚举中添加对应值

## 依赖

| 包 | 用途 |
|---|---|
| HtmlAgilityPack | HTML 解析 |
| Newtonsoft.Json | JSON 解析 |
| Microsoft.Data.Sqlite | 下载历史数据库 |
| CommunityToolkit.Mvvm | MVVM 命令/属性绑定 |

## 免责声明

本软件仅供学习 C#/WPF 技术使用。使用本软件下载内容须遵守相关网站服务条款及当地法律法规。

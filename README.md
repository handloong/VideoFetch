# VideoFetch — WPF Video Downloader

C# WPF 视频下载工具，支持 PornHub / XVideos / XNXX 视频搜索与下载。

## 功能

- 🔍 **关键词搜索** — 在 PornHub / XVideos / XNXX 搜索视频
- 📥 **URL 解析下载** — 粘贴链接自动获取视频信息，支持多画质选择
- ⏬ **批量下载** — 勾选搜索结果批量下载，自动跳过已下载项
- 📊 **表格队列** — 下载队列以表格展示，进度/状态/速度一目了然
- 🗃️ **SQLite 下载记录** — 按 URL 去重，已下载视频显示绿色标记
- 🎬 **双击播放** — 已下载视频双击调用本地播放器，未下载双击预览缩略图
- 🗑️ **强制删除** — 文件占用时标记为重启后删除
- 🎨 **自定义标题栏** — 最小化/关闭按钮，悬停红色效果
- 🌙 **暗色主题** — 紫色系暗色 WPF 界面

## 下载

前往 [Releases](https://github.com/handloong/VideoFetch/releases) 下载最新版。

需要安装 [.NET 8.0 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0)（选 Windows x64）。

## 截图

| 搜索 | 下载 |
|---|---|
| 在搜索页输入关键词 → 勾选结果 → 批量下载 | 下载队列表格展示进度，已完成可播放或删除 |

## 编译

```bash
# 需要 .NET 8 SDK
git clone https://github.com/handloong/VideoFetch.git
cd VideoFetch/VideoFetch
dotnet build -c Release
dotnet run
```

或 Visual Studio 2022 打开 `VideoFetch.sln`。

### 发布

```bash
# 框架依赖（小体积，用户需装 .NET 8）
dotnet publish -c Release -r win-x64 --no-self-contained -p:PublishSingleFile=true

# 自包含（~150MB，无需安装运行时）
dotnet publish -c Release -r win-x64 --self-contained -p:PublishSingleFile=true
```

## 项目结构

```
VideoFetch/
├── Models/Models.cs                # 数据模型
├── Services/
│   ├── ISiteParser.cs              # 解析器接口
│   ├── VideoInfoService.cs         # URL 路由 + 搜索
│   ├── DownloadEngine.cs           # HLS/MP4 下载引擎
│   ├── DownloadQueueService.cs     # 队列管理 + 去重
│   ├── DownloadRecordService.cs    # SQLite 下载记录
│   ├── SettingsService.cs          # 配置持久化
│   ├── LanguageService.cs          # 语言服务
│   └── Sites/
│       ├── PornHubParser.cs
│       ├── XVideosParser.cs
│       └── XNxxParser.cs
├── ViewModels/MainViewModel.cs     # MVVM ViewModel
├── Views/MainWindow.{xaml,cs}      # 主窗口
├── Strings/                        # 多语言资源
├── Themes/DarkTheme.xaml           # 暗色主题
└── Resources/                      # 图标
```

## 技术栈

| 包 | 用途 |
|---|---|
| WPF + MVVM | UI 框架 |
| HtmlAgilityPack | HTML 解析 |
| Newtonsoft.Json | JSON |
| Microsoft.Data.Sqlite | 下载记录 |
| CommunityToolkit.Mvvm | 命令/属性绑定 |

## 免责声明

本软件仅供学习用途。下载内容须遵守相关网站服务条款及当地法律法规。

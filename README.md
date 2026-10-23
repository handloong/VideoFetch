# VideoFetch — WPF Video Downloader

C# WPF 视频下载工具，支持 PornHub / XVideos / XNXX 视频搜索与下载。

## 功能

- 🔍 **关键词搜索** — 在 PornHub / XVideos / XNXX 搜索视频
- ⏳ **时长排序** — 结果列表读得出时长（`10:52` / `11 min` / `13分钟` / `0:02:14` 都能认），
  可一键把长视频排到最前
- 🗂 **分类浏览** — 实时解析站点真实分类/标签（XVideos 38 个、XNXX 155 个、PornHub 105 个、91pinse 热门标签），点标签即浏览
- ⏱ **时长筛选** — 91pinse 支持按 `< 5 / 5-20 / 20+` 分钟过滤，搜索和分类页都能用（下拉框只在该站点出现）
- 📥 **URL 解析下载** — 粘贴链接自动获取视频信息，支持多画质选择
- 🎯 **最高画质** — HLS 主播放列表会展开成 720p/480p/360p 等真实档位，「最佳画质」不会再退化到 360p
- ⏬ **批量下载** — 勾选搜索结果批量下载，自动跳过已下载项
- 🖱️ **右键打开源网址** — 搜索/分类结果右键即可在浏览器打开原页面
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

推送 `v*` 标签即由 GitHub Actions（`.github/workflows/release.yml`）自动打包并发布 Release：

```bash
# 自动递增：在上一个标签基础上 minor +1（v1.5 -> v1.6）
release.bat

# 指定版本（1.8 / 1.8.2 都行，带不带 v 前缀都可以）
release.bat 1.8

# 只看会打成什么版本，不打标签、不推送
release.bat --dry-run
```

工作流会：跑离线测试 → 发布 `framework-dependent` 与 `self-contained` 两个**单文件** exe → zip 后挂到对应 Release。

**版本号唯一来源 = git 标签**，发版不需要改任何源码：标签 `v1.6` → CI 用 `-p:Version=1.6` 注入程序集版本，同时用于 zip 命名。`VideoFetch.csproj` 里的 `<Version>` 只是本地开发的默认值（`publish.bat` 也优先读最新标签，仅在没有标签时回退到它）。

`release.bat` 本身只做三件事：检查工作区干净 → `git tag -a` → 推送分支和标签。它必须是纯 ASCII 的 `.bat`（cmd.exe 按字节解析，中文在 GBK 代码页下会把下一行指令吃掉），所以输出是英文。

本地手动打包（可选）：

```bash
# 框架依赖（~3.5MB，用户需装 .NET 8 Desktop Runtime）
dotnet publish VideoFetch/VideoFetch.csproj -c Release -r win-x64 --self-contained false \
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:IncludeFfmpeg=false -o publish/fd

# 自包含（~70MB，无需安装运行时）
dotnet publish VideoFetch/VideoFetch.csproj -c Release -r win-x64 --self-contained true \
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:IncludeFfmpeg=false -o publish/sc
```

### 测试

`VideoFetch.Test` 里是回归测试，离线部分完全不联网（HTML 夹具取自站点真实页面，下载队列跑在假实现上）：

```bash
# 离线：分类解析 + 下载队列状态机 + 两个页面的布局冒烟（ui-smoke-*.png）
dotnet run --project VideoFetch.Test

# 联网：抓真实分类并逐个浏览首个分类，验证 key 能打开有视频的页面
dotnet run --project VideoFetch.Test -- --cats

# 联网：分类 + 四站点搜索/解析全量集成测试
dotnet run --project VideoFetch.Test -- --live

# 诊断单个链接：打印解析出的画质列表（顺序 + “最佳画质”实际解析到哪条流）
dotnet run --project VideoFetch.Test -- --info "https://www.xvideos.com/video.xxx/title"

# 诊断时长解析：打印四站点真实搜索结果的时长（原始文案 + 秒数 + 长→短排序）
dotnet run --project VideoFetch.Test -- --dur amateur
```

下载队列的测试覆盖：入队/去重/并发上限、失败（含残留文件清理）、取消、重试、删除到回收站、
恢复、彻底删除、清空回收站，以及「哪种状态显示哪个按钮」的可见性矩阵。队列通过
`IVideoInfoProvider` / `IDownloadRunner` / `IUiDispatcher` 三个接缝注入假实现，所以不需要网络也不需要 WPF。

联网测试会读取 `%AppData%\VideoFetch\settings.json` 里的代理配置，和 App 走同一出口。

## 项目结构

```
VideoFetch/
├── Models/Models.cs                # 数据模型
├── Services/
│   ├── ISiteParser.cs              # 解析器接口
│   ├── VideoInfoService.cs         # URL 路由 + 搜索
│   ├── DownloadEngine.cs           # HLS/MP4 下载引擎
│   ├── DownloadQueueService.cs     # 队列管理 + 去重 + 取消/重试/删除状态机
│   ├── DownloadSeams.cs            # 队列的三个可替换接缝（信息/下载/UI 调度）
│   ├── DownloadRecordService.cs    # SQLite 下载记录
│   ├── SettingsService.cs          # 配置持久化
│   ├── LanguageService.cs          # 语言服务
│   └── Sites/
│       ├── CategoryParsing.cs      # 分类/标签 HTML 解析（纯函数，可测）
│       ├── PornHubParser.cs
│       ├── XVideosParser.cs
│       └── XNxxParser.cs
├── ViewModels/MainViewModel.cs     # MVVM ViewModel
├── Views/MainWindow.{xaml,cs}      # 主窗口
├── Strings/                        # 多语言资源
├── Themes/DarkTheme.xaml           # 暗色主题
└── Resources/                      # 图标

release.bat                        # 一键发版：打标签并推送（版本号即标签名）
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

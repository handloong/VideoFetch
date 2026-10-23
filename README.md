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
- 🔁 **退出不丢任务** — 未完成的任务写进 `queue.json`，下次启动自动接着下（失败/已取消的只恢复显示，不会自动重跑）
- 🖱️ **右键打开源网址** — 搜索/分类结果右键即可在浏览器打开原页面
- ☑️ **选中即勾选** — 结果列表支持 Ctrl 点选 / Shift 连选，选中就等于勾选下载，不用逐行点复选框
- 📊 **表格队列** — 表格列：标题 / 时长 / 大小（1.2G、12.3M）/ 进度 / 状态 / 下载时间（年月日时分）/ 操作；
  列宽可拖、表头可换位，标题太长时可横向滚动；队列按下载时间倒序
  点表头即可按标题/时长/大小/进度/状态/下载时间排序（默认下载时间倒序，再点反向）；
  支持**多选**后一次删除多个本地文件，也能一键**重试全部**失败任务
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

**每次推代码前都要打一个新标签** —— 推代码即发版，用 `release.bat`，不要直接 `git push`。

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
# 离线：分类解析 + 下载队列状态机 + 四个页面的布局冒烟（ui-smoke-*.png）
#       以及 UI 巡检：三种窗口尺寸下查裁切/零尺寸/越界/重叠/对比度
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

## 更新日志

版本号即 git 标签，每个版本的安装包见 [Releases](https://github.com/handloong/VideoFetch/releases)。只列功能与修复，细节见提交记录。

### v2.8 — 2026-10-24

- CI 零警告：GitHub Actions 升到 node24 版本（checkout v5 / setup-dotnet v5 / upload-artifact v6 / download-artifact v7）
- 修掉 3 个 C# 编译警告

### v2.7 — 2026-10-24

- 修 v2.6 流水线失败：窗口尺寸断言不再依赖运行机器的屏幕分辨率
- 测试线程补 `DispatcherSynchronizationContext`，避免异步命令的续体在线程池上触碰 UI

### v2.6 — 2026-10-23

- **全项目清扫**（两批）：修 24 处缺陷 —— 详情页标题/时长取不到值、HTTP 超时被当成"用户取消"、200+HTML 错误页被存成视频、重启恢复时误删已下好的成品、语言切换其实从未生效等；补 74 项测试
- **下载链路加固**：读空闲超时（CDN 半死不再永久卡住并发名额）、内容类型校验、HLS `#EXT-X-BYTERANGE` 支持、响应对象正确释放
- 主窗口默认尺寸改为黄金比 1280×791，并按屏幕可用区域自适应
- 语言切换入口从隐藏改为显示、改成即时生效；修 ComboBox 模板导致的"必须断开父 Visual"崩溃
- 站点下拉框顺序：PornHub 第一，其余按字母

### v2.5 — 2026-10-23

- 搜索结果/分类结果支持 Ctrl 点选、Shift 连选，选中即勾选下载
- 新增 UI 巡检：三种窗口尺寸下检查文字裁切、零尺寸、越界、元素重叠、文字对比度（已接入 CI 断言）
- 强调色压深（`#7C6FF7` → `#5B4CD8`），紫底白字对比度从 3.0:1 提到约 6:1

### v2.4 — 2026-10-23

- 下载队列支持点表头排序（标题/时长/大小/进度/状态/下载时间，再点反向）
- 支持多选后一次删除多个本地文件（带确认框，只处理已完成且有文件的任务）

### v2.3 — 2026-10-23

- 内置播放器加自愈：进度不动就自动重试开播（最多 3 次）
- 播放/失败各记一条日志，方便定位

### v2.2 — 2026-10-23

- 修横向滚动条被压成 6px 白块（滚动条样式宽度不该无条件写死）
- 滚动条换成暗色模板，不再用系统浅色外观

### v2.1 — 2026-10-23

- 列宽真的拖得动了（自定义模板缺 `PART_HeaderGrip`，且 WPF 内置 resize 钩子不生效，改为自管 `DragDelta`）
- 退出后未完成任务自动继续下载；损坏的 `queue.json` 不会拖垮启动

### v2.0 — 2026-10-23

- 修调整"最大同时下载数"后保存报错（信号量上限被写死成初始值）
- 新增「大小」列（1.2G / 12.3M），表头可拖、横向滚动条可用

### v1.9 — 2026-10-23

- 下载队列按下载时间倒序（最新的在最上面）
- 修掉已完成行下面多出来的一行英文 "Done"

### v1.8 — 2026-10-23

- 修内置播放器双击后不自动播放（播放命令在窗口加载前被丢弃）
- 记住音量，下次打开沿用

### v1.7 — 2026-10-23

- 下载管理新增「时长」「下载时间」两列；历史记录补 `Duration` 列（老库自动迁移）

### v1.6 — 2026-10-23

- 下载页「重试全部」、结果列表按时长排序、91pinse 按时长筛选
- 支持导入站点 Cookie，解锁登录态才有的高清画质
- 全局异常兜底 + 日志文件；右键菜单/ToolTip 补齐暗色样式
- 修同名视频抢同一个输出文件、XVideos 只有 360p、pinse 取不到流
- 补齐下载引擎与四站点搜索解析的离线测试

### v1.5 — 2026-06-26

- 重写分类抓取：改为解析站点真实分类/标签
- 内置播放器改进；发布脚本调整

### v1.4.0 — 2026-06-26

- 新增 91Pinse 站点支持
- 修未按网站名称分类、标题栏按钮异常

### v1.3.0 — 2026-06-15

- 版本号规范化（无功能变更）

### v1.2 — 2026-06-11

- 内置播放器、下载历史、保存目录选择、动态版本号
- MainWindow 拆分为 4 个 UserControl

### v1.1 — 2026-06-10

- UI 布局重构、下载记录去重、应用图标
- win-x64 自包含发布

## 免责声明

本软件仅供学习用途。下载内容须遵守相关网站服务条款及当地法律法规。

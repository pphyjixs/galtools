# galtools 架构设计

日期：2026-10-14。状态：目标架构与已实现原型并存。当前实现使用本机 .NET Framework 4.8 编译器、WPF 界面、XML 游戏清单和 GARbro-Mod 独立桥接进程。界面规范见 UI-DESIGN.md；Theme.xaml 与 Shell.xaml 嵌入 exe。下文 .NET 10 / SQLite 等仍是后续演进方案，WPF 界面现已实现。

## 目标与边界

galtools 是 Windows 10/11 x64 桌面软件，当前有两个功能域：

1. **Gal 管理**：新建游戏、记录安装目录和封面、绑定一个或多个游戏 EXE、设置启动参数与工作目录，并从游戏详情页启动。游戏可以关联多个资源目录与首选解包方案。数据库只存元数据，不改动游戏文件。
2. **资源浏览与处理**：像文件管理器一样进入文件夹和封包，列出内部文件、预览图片与音频、保存原文件，或转换图片为 PNG、音频为 MP3 等格式后导出。允许打开未添加到游戏库的任意文件夹。

界面的布局、主题和具体交互留待后续讨论。本设计先固定业务与后端边界。封包可打开、条目可读取、媒体可预览、媒体可转换是四种不同能力，UI 分别展示，不能只凭扩展名宣称支持。

## 技术与部署方案

| 层 | 方案 |
| --- | --- |
| 主程序 | C#、.NET 10、WPF；视图使用 MVVM，页面不直接调用解包库。 |
| 数据 | SQLite 保存游戏与设置；预览缩略图单独缓存。 |
| 解包 | 后端进程隔离，通过版本化协议通信。GARbro 与 GARbro-Mod 各在私有目录运行，避免同名 GameRes 等 DLL 冲突。 |
| 转换 | 独立的媒体解码与编码服务；先解出条目流，再按实际内容识别格式。 |
| 发布 | 第一版交付便携 ZIP：一个入口 EXE 加项目内私有运行文件，解压即用。验证后再尝试单文件 EXE；单文件模式的原生组件仍可能需运行时释放。 |

.NET 10 是当前的 LTS。GARbro-Mod 源码目标为 .NET Framework 4.7.2，因此不能预设其 DLL 能直接加载到 .NET 10。开发初期可用独立旧框架桥接进程验证功能；正式发布前必须验证现代 .NET 移植，或在目标干净 Windows 系统上验证桥接进程不需额外安装运行时。这是发布门槛，不是已经解决的兼容问题。

## 项目分层

```text
galtools/
  src/
    Galtools.Desktop/          # WPF 窗口、视图模型
    Galtools.Application/      # 游戏、浏览、预览、导出任务编排
    Galtools.Domain/           # Game、Archive、Entry、Media 等模型
    Galtools.Contracts/        # 后端协议与错误码
    Galtools.Storage/          # SQLite、设置、缓存元数据
    Galtools.BackendHost/      # 后端发现、选择、进程和 IPC
    Galtools.Media/            # 格式嗅探、解码器、预览、编码器
    Backends/Garbro/           # 原版 GARbro 适配器
    Backends/GarbroMod/        # GARbro-Mod 适配器
    Backends/Cxdec/            # 后续接入现有 Cxdec 和 C 解码器
  vendor/                     # 固定版本的第三方源码及许可证
  tests/                      # 样本封包、协议、转换和安全验证
  scripts/                    # 构建、发布、干净环境验证
  docs/
```

依赖方向是 Desktop → Application → Domain/Contracts。Storage、BackendHost、Media 实现 Application 定义的接口。后端进程只依赖 Contracts，不引用 UI 或数据库。解包引擎、媒体解码器、输出编码器三类插件分别注册；增加新封包工具不需改动前端。

## 解包后端协议

每个后端声明 `id`、版本、来源提交、所需上下文和支持的能力。协议至少包含：

- `Probe(path, context)`：读少量文件头并评估候选；返回格式、可信度、证据，以及是否需要游戏 EXE、密钥或方案。扩展名只用于排序。
- `Open(path, context, options)`：打开封包，返回会话 ID、实际格式、所用方案和能力。失败需区分“不支持”“需要参数”“密钥错误”“文件损坏”“后端崩溃”。
- `List(session, page, filter)`：分页列目录。条目有稳定 ID、原始名称、规范化虚拟路径、压缩大小、解压大小与类型提示。
- `Read(session, entryId, offset?, length?)`：按需传送解包后的字节流；后端不直接写用户的导出位置。
- `DecodeImage/DecodeAudio`（可选）：调用后端独有解码器，返回像素或 PCM 等中间数据及尺寸、声道、采样率。
- `Close/Cancel`：释放会话、取消任务；长操作报告进度。

控制消息包含协议版本、请求 ID、错误码；大数据走二进制流或受控临时文件，避免将整个条目装入 JSON/Base64 或内存。后端工作进程设置超时、可取消、可重启。GARbro 适配器调用 `FormatCatalog`、`ArcFile.TryOpen`、`OpenEntry` 和 `OpenImage`。原版与 Mod 分开运行；`Formats.dat`、游戏 EXE 识别、密码/方案询问应进入协议，不能把“需要方案”误报为“不支持”。

## 识别与后端选择

进入文件夹时先列普通文件；点击疑似封包时再正式探测。先按签名和少量结构选择候选后端，再结合所绑定游戏、上次成功的后端与方案排序；冲突时允许手动切换。探测缓存以文件路径、大小、修改时间和后端版本为键，源文件变化后失效。只有成功打开并列出条目才标记“可打开”，不因 `.xp3`、`.pak` 等后缀就宣称支持。

绑定的游戏 EXE 只是解密上下文。浏览或解包不会运行游戏。后端若要求额外参数，统一向 UI 返回“需要参数”，用户选择后可记在该游戏的首选后端配置里。

## 新游戏支持的维护方式

GARbro-Mod 将格式解析代码（`Arc*.dll` 中由 `FormatCatalog` 注册的封包、图片和音频处理器）与游戏方案数据（`Formats.dat` 中的 `SchemeMap` 和 `GameMap`）分开。XP3 的通用容器只需要一个解析器；不同游戏可能使用不同加密过滤器、密钥、索引扰乱或文件名映射，因此游戏级支持不等于“每部游戏都写一个新解包器”。

galtools 维护一份**可审阅的游戏支持清单**：游戏 ID、可选 EXE/封包特征、候选后端及版本范围、方案 ID、所需参数、来源与验证样本。识别只给候选，不以 EXE 文件名作为唯一证据；用户可手动覆盖。清单与 GARbro-Mod 自身的 `Formats.dat` 版本分别记录，不能假定新清单能使旧后端理解新算法。

新增作品按所需改动分级：

1. 已有解析器和方案：仅增加游戏识别规则及方案映射，核实列目录、抽样内容和预览。
2. 已有算法但参数不同：增加密钥、控制块、文件名表等方案数据，生成对应后端所需数据库，做游戏样本回归。
3. 新加密/索引变体：在现有 XP3 等解析器下增加算法实现及方案，再做上述映射；如 GARbro-Mod 无法适配，也可接入专用后端。
4. 全新封包容器或媒体编码：实现新的封包处理器或解码器，并通过同一后端协议暴露。

每项支持附一个最小回归样本或可公开的测试夹具，分别验证探测、目录数量、抽样文件头、完整导出和媒体预览。GARbro-Mod 当前 `Formats.dat` 是压缩的二进制序列化数据，其 `SchemeTool` 通过代码编辑并重新序列化；galtools 自己的游戏清单采用文本格式维护，构建时再生成或引用目标后端能读取的方案文件，不直接让用户编辑二进制数据库。外来 `Formats.dat` 只作为受信任的随版本打包文件加载。

## 预览与转换流水线

`封包条目 → 解包流 → 文件头嗅探 → 解码器 → 预览或输出编码器`。普通磁盘文件从“文件头嗅探”开始复用后半段。扩展名缺失或错误时仍尝试内容识别，例如 TLG、TGA、PNG、JPEG、WebP 和受支持的音频。解码失败仍可保存原文件。

预览使用独立队列，切换选中项时取消旧任务，限制并发并缓存缩略图。缓存键包含封包版本、条目 ID 和解码器版本。显示目标可设为约 480p；有些格式必须先全尺寸解码再缩小，因此**低分辨率显示不保证缩短解码时间**，只有支持缩放/分块解码的格式才能直接省去部分工作。列表不预解码所有条目。

导出命令分为“保存原文件”“转换后保存”“批量导出”。原文件按位保留后端结果；PNG/MP3 从完整解码数据生成，不能拿预览缓存导出。PNG 保留可用的透明通道；MP3 暴露质量/码率设置并报告逐项失败。编码器可采用项目内 FFmpeg 构建，但需核实其 LGPL/GPL 配置、来源和再分发文件。

导出前拒绝绝对路径、盘符、`..` 穿越及符号链接越界；文件名冲突由用户选覆盖、跳过或重命名；先写临时文件再移动。对异常大的解压尺寸、条目数量和图片尺寸设置限制，防止损坏封包耗尽内存或磁盘。

## 游戏库数据

基础实体：`Game(id, title, installPath, coverPath, notes)`、`LaunchProfile(id, gameId, exePath, args, workingDir, isDefault)`、`ResourceLocation(id, gameId, path)`、`BackendPreference(gameId, backendId, schemeId, lastSuccess)` 和标签关联。启动游戏只由用户主动点击触发。游戏目录搬迁后允许重新定位并修复相对 EXE/资源路径；数据库不存游戏本体或完整解包文件。

## 依赖与 Release

第三方组件记录固定版本、来源、许可证与构建方式。运行时只从 galtools 发布目录加载后端和转换器，不搜索开发机上的其他 GARbro 安装目录。发布包包含自包含主程序、各后端私有文件夹、`Formats.dat`、原生 DLL、许可证和版本清单。构建脚本统一产出 `win-x64` 便携 ZIP。

普通用户下载 Release ZIP 后可解压运行。源码 clone 的开发者使用项目内一键构建脚本恢复固定依赖并构建，不需手动从其他项目复制 DLL；构建 SDK 如何取得要由脚本解决或明确作为开发前提，不能影响 Release 用户。

## 实施与验收顺序

1. 建解决方案、协议、数据库和最小目录浏览。
2. 验证 GARbro-Mod：编译、列目录、读取、图片解码、游戏方案询问；同时做原版 GARbro 的独立适配，并实测运行时兼容问题。
3. 接入现有 Cxdec、TLG 和 C 解码能力；实现后端选择与解码器注册。
4. 完成预览缓存、原文件导出、PNG/MP3 转换、进度与取消。
5. 完成新建游戏、EXE 绑定与启动、资源目录关联。
6. 在未预装 GARbro、Python、Rust、FFmpeg 的干净 Windows 机器上验证 Release ZIP：启动、打开封包、预览、导出、转换和游戏启动。

回归样本至少覆盖普通 GARbro 封包、需方案的 GARbro-Mod 封包、Cxdec XP3、TLG/TGA 图片与音频条目；同时验证错误提示、路径安全、连续切换预览、取消和大文件内存使用。

## 依据与待验证项

- 本地 [ArcPreview 桥接](../../ArcPreview/garbro_bridge/Program.cs)目前只实现列目录与整项 `OpenEntry` 读取；新协议需增加 `OpenImage` 和方案询问。
- 本地 GARbro-Mod 当前提交为 `c23bd588`（2026-10-12），项目目标是 .NET Framework 4.7.2；现代 .NET 移植及实际兼容性尚未测试。
- [.NET 10 支持周期](https://learn.microsoft.com/en-us/dotnet/core/releases-and-support)、[Windows 桌面应用发布](https://learn.microsoft.com/en-us/windows/apps/package-and-deploy/publish-first-app)、[.NET Framework 库移植限制](https://learn.microsoft.com/en-us/dotnet/core/porting/framework-overview)。
- [FFmpeg 官方再分发说明](https://ffmpeg.org/legal.html)。

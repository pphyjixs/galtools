# 新增游戏与封包支持流程

galtools 把“封包结构”、“游戏密钥”和“资源解码”分开维护。扩展前先判断缺的是哪一层；同一个扩展名可能是完全不同的文件结构，不能只凭 `.paz`、`.xp3` 判断。

## 1. 收集样本并定位缺口

1. 记录游戏目录、启动 EXE、封包文件名和大小；保留多卷文件（例如 `bg.pazA`）。只读检查每类文件的前 32–64 字节、末尾索引和少量条目，不改动游戏原文件。
2. 在 `vendor/GARbro-Mod/ArcFormats` 查扩展名、文件头签名和 `ArchiveFormat` 实现；同时检查 `runtime/garbro-mod/GameData/Formats.dat` 是否已有作品名、密钥和 EXE 映射。
3. 用 `runtime/garbro-mod/Galtools.GarbroModBridge.exe 封包路径 [方案名]` 验证：是否能列目录、原文件 `R` 是否有正确文件头、图片 `P/I` 或音频 `A/M` 是否能解码。仅能列目录不等于成功解包。
4. 逐个核对同游戏的封包。有的“汉化补丁封包”沿用原版密钥，有的只有扩展名相同、结构不同。

## 2. 可复用现有处理器：更新 Formats.dat

- 如果索引字段、数据流加密/压缩方式和已有 `ArchiveFormat` 一致，只补游戏方案。密钥及按封包名区分的 `IndexKey`/`DataKey` 放在 `Formats.dat`；文件名映射放在其中的游戏映射。多卷文件仍交给原处理器。
- `Formats.dat` 是 GARbro-Mod 的二进制序列化数据库，不直接用文本编辑器修改。通过项目内 `src/Galtools.FormatData/Program.cs` 和 `scripts/update-formats.ps1` 从已加载的方案生成新数据库。新作品可以仿照该工具新增一条幂等更新命令；若数据库没有密钥，先取得并验证每个封包的密钥，再写入 `PazScheme` 等对应类型。
- 修改后核对自动识别与显式指定方案两种入口。若需要用户手选，把该格式的方案列表和 `ParametersRequest` 选项接到 `Galtools.GarbroModBridge`；界面现有“方案”字段会作为第二参数传入。若文件头足够独特，也可以像本游戏一样把精确签名映射到方案，但需先排除与其他游戏撞签名。
- 数据库、更新源码和构建脚本必须随项目提交，运行时不依赖机器上的 GARbro 安装。

## 3. 结构特殊：新增解包后端

1. 在 `src/Galtools.Desktop` 实现 `IArchiveBackend`：`Open` 列出条目，`ReadImagePreview` 提供 480p 左右的预览，`ReadImagePng` 保留完整尺寸，`Save("R", …)` 保存解包后的原文件；不支持的转换明确抛出异常。
2. 在 `BackendRegistry` 注册工厂。按文件头严格验证，避免同扩展名被错误后端抢走；边界检查索引、偏移、长度、条目数与文件名。若解码器依赖本机外部库，先把依赖封装到项目运行目录并更新 `scripts/build.ps1`。
3. 在文件浏览页为新扩展名标注“封包”。导出路径必须防止绝对路径、`..` 和非法文件名；批量导出保持原始相对目录。
4. 对真实样本至少检查：目录数量、一个原文件的签名或可读内容、一次预览/转换、一次导出；再构建便携包，确认它包含新后端和更新的数据库。速度或兼容性结论只针对已验证样本。

## 本次：《永不落幕的前奏诗》

测试对象：《永不落幕的前奏诗（ソレヨリノ前奏詩）》的游戏目录。

| 文件 | 判断与处理 |
| --- | --- |
| `bg/bgm/mov/scr/se/st/sys/voice.paz`，以及 `bg.pazA` 等分卷 | minori 的 Musica PAZ v2。文件头 `F2 E4 F9 E2` 是 `yori` 与 `8B` 异或后的结果。GARbro-Mod 原数据库已有 `Soreyori no Prologue` 密钥；本次把该签名与 EXE 映射补进 `Formats.dat`。继续复用 `PazOpener` 和其多卷、Blowfish、RC4、zlib 处理。 |
| `cnscr.paz`、`cnsys.paz` | 汉化补丁仍使用 Musica PAZ。分别为 `scr`、`sys` 的密钥增加封包名别名，并写入 `Formats.dat`。 |
| `cnbg.paz` | 文件头是 `FUYINPAK`，与 Musica PAZ 不同。新增 `FuyinPakBackend` 解析尾部索引和 BMP 数据，支持软件内预览、原文件导出和 PNG 转换。 |

验证结果：`bg.paz` 列出 5,213 个条目，抽样 PNG 能保存原文件并生成预览；`bgm.paz` 的 OGG 可导出并转换 WAV/MP3；`cnsys.paz` 的 PNG 可导出和预览；`cnbg.paz` 列出 40 张 BMP，抽样导出和转换成功。`cnbg` 的数据不经过 GARbro-Mod，因此没有把它的解析逻辑错误地写入 Musica PAZ 的方案表。

已用 `scripts/extract-soreyori.py` 实际导出：11 个主封包合计 21,078 个文件、7,862,832,274 字节，跳过 0 个，空文件 0 个。抽查 PNG、OGG、BMP、AVI 文件头正确。这里的“主封包”包含 `bg.paz` 和 `st.paz` 自动合并读取的 `.pazA` 等分卷。

更新数据库、构建与打开方法：

```powershell
# 在 galtools 仓库根目录执行
.\scripts\update-formats.ps1   # 当前项目已执行；重跑是幂等的
.\scripts\build.ps1
```

打开 `galtools.exe` → 资源浏览 → 打开文件夹 → 选游戏目录 → 双击 `.paz`。`bg.pazA` 等分卷由 `bg.paz` 自动读取；不需要单独打开。选择图片后可预览、保存原文件或转换 PNG；“导出全部”导出当前封包。`scripts/extract-soreyori.py` 可一次导出该游戏所有 `.paz`，它是可选的 Python 批处理入口；桌面软件本身无需 Python。

方案密钥与 PAZ 结构的参考实现：项目内 `vendor/GARbro-Mod/ArcFormats/Musica/ArcPAZ.cs`，以及 [FuckGalEngine 的 minori PAZ 工具源码](https://github.com/Inori/FuckGalEngine/blob/master/Minori/Minori/fuckpaz/main.cpp)。

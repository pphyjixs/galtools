# galtools UI 设计方案

## 设计方向：蓝粉收藏馆

以用户提供的四张参考图为主要视觉依据：固定侧栏、三列流动封面、白色圆角卡片、游戏详情弹层、资源目录 / 文件 / 预览三栏。借鉴同类产品的信息组织，不复制站点素材。

- Hikarinagi 游戏图鉴：https://www.hikarinagi.org/galgames/ 。参考作品集合、分类及筛选入口。
- LunaBox：https://box.lunarain.site/ 。参考以游戏库为中心的分类、搜索和管理结构。
- 月幕网站本次访问返回 503，未作为已验证的视觉依据。

## 设计规范

| 项目 | 规范 |
| --- | --- |
| 背景 | 雾白 #F7F8FD，局部浅蓝 / 浅粉渐变，不铺满饱和色 |
| 主文字 | 深蓝灰 #1D2C49，次要信息 #76839B |
| 操作 | 海盐蓝 #468CFA，主按钮使用轻微蓝色渐变 |
| 辅助色 | 樱花粉 #E977A9、淡紫 #9380D8、薄荷 #48AB9A |
| 字体 | 本机 Microsoft YaHei UI，正文 13–14 DIP，页面标题 26 DIP |
| 图标 | 统一矢量线条，1.6–1.8 DIP，不混用 emoji / 系统符号 |
| 圆角 | 输入和按钮 10 DIP、卡片 16 DIP、详情弹层 20 DIP |
| 间距 | 8 DIP 基础节奏；区域间 20–28 DIP；避免控件贴边 |
| 控件状态 | 悬停、焦点、选中、按下、禁用统一模板；键盘焦点可见 |

## 页面与交互

游戏页：页面标题和数量 → 搜索 / 标签 / 排序 / 添加 → 三列封面流。两侧向上、中间向下，速度错开；每一轮覆盖筛选后的全部游戏，小库允许重复；点击卡片暂停并打开详情，关闭后按原位置继续。显式暂停按钮、离开游戏页和窗口失焦均暂停动画。卡片直接启动按钮执行启动，卡片其他区域进入详情。

详情：横幅封面、名称、标签、路径、启动程序、解密方案、备注；启动为主操作，浏览资源 / 编辑为次操作。编辑沿用相同输入、按钮和弹层样式。

资源页：导航与方案选择 → 搜索及类型筛选 → 目录 / 文件 / 预览三栏 → 导出工具栏。列表保持虚拟化；宫格只为可见条目按需加载缩略图，缓存有上限；预览和保存完整文件保持分离。展示真实数据，不填虚构资源数量。

## 清晰度与工程

旧 WinForms 版本缺少 DPI 配置，存在系统位图放大造成整体模糊的风险。界面迁移至本机 .NET Framework WPF：矢量图标、DIP 布局、UseLayoutRounding、SnapsToDevicePixels、Display 文本格式化和 DPI manifest。封面按目标尺寸解码并缓存，不把整页渲染成低分辨率位图。资源预览仍使用 480p，导出保留完整质量。

界面资源编译进 exe；继续使用原 GameStore、BackendRegistry 与 GARbro-Mod 桥接协议。构建只使用本机 csc 和 WPF 程序集，不引入 npm、WebView 或新下载的 UI 框架。

参考：https://learn.microsoft.com/en-us/dotnet/framework/wpf/graphics-multimedia/wpf-graphics-rendering-overview
https://learn.microsoft.com/en-us/dotnet/desktop/wpf/advanced/layout?view=netframeworkdesktop-4.8

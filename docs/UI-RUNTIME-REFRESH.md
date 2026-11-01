# 启动器设置、运行环境与外观

本地候选版本，2026-10-31。保留此前资源下载和客户端更新改动；未提交、推送、部署或修改线上服务。此文描述最终行为，替代第一版需要点击“保存设置”的设计。

## 使用方式

- **设置 → 游戏设置**：游戏 Java 支持自动选择、本机检测列表和自定义路径。列表显示版本、架构、厂商，选择后自动验证并保存。游戏内存支持自动分配、手动输入和滑块；单位为 MiB，切换自动模式仍保留最近的手动值。
- **设置 → 外观**：主题模式支持跟随系统设置、浅色模式、深色模式。标题栏按钮直接切换亮暗，保留当前页面、设置标签、滚动位置和背景图片。
- **背景图片 → 自定义**：可同时选择多张本地 PNG/JPEG/BMP，最多 50 张；图片复制到启动器自己的数据目录。支持删除列表项、立即切换、自动切换、随机顺序及 10～3600 秒间隔。删除列表项不会删除用户原图。
- **背景图片 → 网络**：输入直接图片链接或直接返回图片的 API，例如 `https://acg.neko-arc.top/api.php`。支持 HTTP 重定向、定时刷新和本地缓存。切换主题或调节不透明度不会重新请求图片。
- **游戏目录、更新**：隔离模式、自动更新选项修改后立即保存。目录通过选择器修改；已有实例需要迁移时，仍确认实际移动操作，完成后保存目录。

普通设置没有保存按钮。有效改动独立保存并生效；正在输入的不完整 Java 路径或非法内存值不会覆盖上一有效设置，也不会阻止其他选项保存。写入失败显示常驻错误和“重试”，保留待保存改动，不能把失败写入视为成功。关闭窗口前会完成当前 Java 路径的验证。

## 界面

参考 HMCL 的“游戏 Java、游戏内存、自动分配内存、主题模式、背景图片”等命名。参考 CC Switch 的中性色、轻边框、紧凑设置分组和左右对齐的选项布局，移除上一版额外创作的宣传文案。卡片圆角 6，输入框圆角 4，文本内边距只应用一次。

一张背景绘制在整个主窗口底层，包含标题栏、侧栏及内容区；标题栏、侧栏、设置卡片使用半透明表面保证文字可读。新建实例页面不另铺不透明底色。登录及其他窗口共用主题与背景资源。下拉菜单使用不透明底色，以保证选项可读。

## 实现边界

`MainWindow.AutoSave.cs` 负责独立字段提交、失败重试及目录迁移；`MainWindow.Preferences.cs` 负责 Java、内存和主题；`MainWindow.Background.cs` 负责背景设置界面。

`AppearanceManager` 管理动态颜色和全窗背景，`BackgroundController` 管理轮换、异步请求取消和代次检查，`BackgroundLoader` 负责有界图片下载。网络图片限制 20 MiB、25 秒、最多 5 次重定向；拒绝携带用户信息的 URL、非 HTTP(S) 协议、HTTPS 降级和非图片内容。不携带启动器账号、Cookie 或鉴权头。图片验证后才替换缓存；失败保留上一张可用图片。离线重启可加载已有缓存；迟到的旧请求不能覆盖新选择。

网络地址必须直接返回 WPF 可解码的图片，或者重定向到图片。不解析返回图片 URL 的 JSON，不将网页截图作为背景，不支持视频或动画播放。图片最大 1 亿像素，解码后的最长边不超过 1920。旧 `BackgroundPath` 自动迁移为单张本地图片列表；设置加载不会主动重写文件。

Java 自动选择按游戏元数据匹配主版本，优先兼容的 64 位；本机缺少时继续使用已有 Mojang 官方运行时下载流程。备用版本映射：1.16 及之前 → 8，1.17 → 16，1.18～1.20.4 → 17，1.20.5～1.21.x → 21，26.1 → 25。手动选择检查最低主版本和架构。

自动内存取物理内存一半与当前可用内存减系统预留的较小值，按 256 MiB 向下取整，范围 512～8192 MiB；32 位 Java 上限 1024 MiB。手动值校验物理内存和 Java 位数，实际启动选项使用 `MemoryAllocation.Resolve`。旧配置保留手动值，新配置默认自动。

## 验收与预览

独立 WPF 测试项目 `tests/LauncherSettings.Tests` 使用真实窗口、控件事件、签名示例清单和隔离数据目录。正式客户端没有新增跳过登录开关。验收覆盖即时保存、无效值、保存失败重试、重开初始化、关闭前 Java 验证、主题切换不跳页、最小窗口布局、整窗背景、多图轮换、真实计时器、网络缓存及请求竞态。

```powershell
dotnet run --project tests/LauncherSettings.Tests -- <仓库绝对路径> <新的验收目录绝对路径>
# 额外检查用户指定的真实图片 API：
dotnet run --project tests/LauncherSettings.Tests -- <仓库绝对路径> <新的验收目录绝对路径> --live-background
```

附加 `--preview` 打开独立开发预览，可操作 Java、内存和外观。示例整合包安装、真实账号和更新被禁用。数据只写到验收目录内的 `isolated-data`。测试 Java 使用受控元数据，不启动真实游戏；本轮没有实际游戏登录/启动及操作系统主题切换的人工验收。

当前构建和日志位于 `F:\Projects\all\csumc\ui-autosave-20261031`：`client` 为正式候选，`preview-app` 为独立预览，`release-acceptance` 为发布构建截图。`启动开发预览.ps1` 复用已安装的本地 .NET 10 Windows Desktop 运行时。`before-autosave.zip` 是本轮修改前备份；`ui-refresh-20261031` 仅为上一版历史快照。

## 参考

- [HMCL 中文命名](https://github.com/HMCL-dev/HMCL/blob/main/HMCL/src/main/resources/assets/lang/I18N_zh_Hans.properties)
- [HMCL 游戏设置](https://github.com/HMCL-dev/HMCL/blob/main/HMCL/src/main/java/org/jackhuang/hmcl/ui/game/GameSettingsPage.java)
- [HMCL 个性化设置](https://github.com/HMCL-dev/HMCL/blob/main/HMCL/src/main/java/org/jackhuang/hmcl/ui/main/PersonalizationPage.java)
- [CC Switch 样式](https://github.com/farion1231/cc-switch/blob/main/src/index.css)
- [CC Switch 设置布局](https://github.com/farion1231/cc-switch/blob/main/src/components/settings/SettingsLayout.tsx)

实现沿用本项目 WPF / CmlLib 架构，未引入 HMCL 或 CC Switch 的运行时组件。

最终结果：设置与背景验收 93 项、资源下载回归 62 项、更新协议回归 70 项通过。真实网络 API 已另行实测。最终发布构建截图见 `final-acceptance`，网络 API 截图见 `release-acceptance/network-background.png`，候选与预览 DLL 哈希相同。

## 2026-10-31 界面细节调整

整合包信息标签顺序调整为游戏版本/加载器、大小、整合包版本、服务器；版本不再悬在标题右侧。标题栏移除背景切换入口，保留外观设置中的手动切换按钮。主题及三个窗口控制按钮使用 14 像素矢量图标、32×30 点击区域；太阳/月亮参考 https://www.yeqing.net/ 左下角 Lucide 图标，并附带 `CMUmcl/Licenses/Lucide.txt`。

新配置以及未记录自动切换选项的旧配置默认关闭自动轮换，已保存的明确选项继续保留。候选、预览和验收截图位于外层 `ui-polish-20261031`。

2026-10-31 可读性修正：标题栏分割线独立使用 SeparatorBrush 并对齐像素；设置标签栏增加半透明底色，避免文字直接叠在图片上。提升辅助文字对比度及内容表面不透明度，保留用户的背景图片不透明度数值。使用当前预览缓存图片、100% 背景不透明度检查亮暗效果，截图和验收记录位于 `ui-contrast-20261031/acceptance`。

代理设置及内存/磁盘占用快照见 [PROXY-SETTINGS.md](PROXY-SETTINGS.md)。当前预览入口移至 `F:\Projects\all\csumc\proxy-settings-20261031\启动开发预览.ps1`。

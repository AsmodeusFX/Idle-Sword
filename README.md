# Idle-Sword · 土豆天尊

Windows / Godot .NET / C# 横版修仙增量放置原型。

## 启动

1. 安装 **Godot 4.7 stable .NET**（引擎版本串为 `4.7.stable.mono`）和 **.NET 10 SDK**（本机已验证：10.0.301）。
2. 在 Godot 项目管理器中导入 `idle-sword/project.godot`。
3. 初次运行需要还原 Godot C# NuGet 包。在仓库根目录执行：

```powershell
dotnet build idle-sword/Idle-Sword.csproj
```

4. 在 Godot 中按 **F5**。默认窗口 1280×720，设计尺寸 1920×1080，保持 16:9 等比缩放；宽高比不同则留边。

## 当前可体验

- 角色地面移动、范围内停步、自动释放法术；**六种怪物原型**（近战 / 远程 / 肉盾 / 高速 / 飞行近战 / 飞行远程）。另有每秒一发的**普通攻击**（平射飞剑），法术中的三个"神通"改为普攻时按概率触发。
- 怪物分**地面与空中**两层，技能声明能打哪一层：**飞行单位免疫只打地面的技能**（普攻不限层，所以不会卡死）。
- 每关 25 格、当前格刷怪、**一条波次混编多种怪**、超时叠怪、越过刷怪点停刷。**每波数量随关卡递增且递增在加快**，各模板有预设上限（约第 80 关触顶）。
- 普通格死亡重返本关起点；BOSS 格原地重生并保留敌人伤势与死亡状态。
- BOSS 死后停止补怪，清完敌人解封裂隙，击碎后传送。
- 每关 BOSS 首杀固定 1 灵核，重复击杀不掉灵核；旧关和最终关整关循环。
- 修行节点、5 个境界/15 个样例技能、单武器打造/淬炼/洗练、4 类参悟/强化、3 类剑灵/增强。
- 在线自动参悟通过修行“生根”解锁；产物移入鼠标收取，切页不影响生产。
- 5 秒自动保存、关键操作保存、正常关闭保存、原子替换及上一份备份。

界面右侧关卡下拉框选择已解锁关卡，选择后进入整关循环；上方按钮切换循环与自动推进。

**这是可运行框架原型，技能、天赋、装备、抽取规则和数值仍是样例。** 武器暂时直接替换，不含多实例背包；参悟强化暂以效果倍率为主。100 关已配置，尚未完成整体平衡与长尾内容。详见 [阶段交付说明](docs/planning/phase_01.md)。

## 验证与配置导出

从仓库根目录执行：

```powershell
dotnet run --project tests/IdleSword.Checks.csproj -- idle-sword/Config/Tables
dotnet run --project tests/IdleSword.Checks.csproj -- idle-sword/Config/Tables --export artifacts/csv-roundtrip
```

检查不需要启动 Godot。第二条先验证，再将配置通过 CSV 写入器导出到独立目录，并重新加载校验。修改源 CSV 后重启游戏生效，不运行时热重载。

Godot 引擎验证（将 `godot` 替换为本机 .NET 编辑器可执行文件）：

```powershell
godot --headless --path idle-sword --editor --import
godot --headless --path idle-sword -- --smoke-test
```

`--smoke-test` 使用独立的新会话、不写正常存档，遍历页面并测试天赋按钮与鼠标收取信号。`--capture <绝对PNG路径>` 在带渲染的同类验证中生成各页面截图，输出目录需已存在。

音频素材由 `tools/SoundGen/` 程序化生成，产物已入库，正常开发不需要重跑：

```powershell
dotnet run --project tools/SoundGen -- idle-sword/Assets/Audio
```

改过音频或拉到含新音频的提交后，`--import` 这一步是必须的（`.godot/` 不入库），否则运行时加载不到素材。

## 存档与导出

- 游戏使用 Godot `user://save_v1.json` 和 `save_v1.json.bak`。Windows 默认位置为 `%APPDATA%\Godot\app_userdata\Idle-Sword\`。
- 存档不在 Git 仓库中，换电脑不会通过 Git 自动携带试玩进度。
- 无离线收益。重进恢复保存时的关卡、位置、敌人状态与技能冷却；短期弹丸、区域、召唤物与战斗 Buff 会清理。
- `export_presets.cfg` 配置 Windows x86_64 导出与原始 CSV/资源映射包含规则，发行导出由下方的一条命令完成。

## 打包 Windows 发行版

一条命令产出**单个自包含**的 `artifacts/windows/Idle-Sword.exe`——引擎、PCK、C# 程序集全在这一个文件里，拷走单独双击即可运行：

```powershell
powershell -File tools/PackWindows.ps1 -InstallTemplates
```

`-InstallTemplates` 只有首次需要：脚本从 Godot 官方 release 下载对应版本的导出模板（约 1.1 GB），校验 SHA256 后**只解开 Windows 那一个模板文件**，之后重跑不再需要下载。

脚本依次执行：前置检查（编辑器是 .NET 版且版本与工程一致、导出模板齐备、`Idle-Sword.sln` 存在、dotnet 可用、预设已启用单文件内嵌）→ 编译（警告即错误）→ 纯逻辑自检 → `--import` → 导出 → 产物断言 → **把打出来的 exe 拷到仓库外单独跑一遍**。

最后一步是重点：Godot 的 `--export-release` 在导出失败时**也返回 0**，只盯退出码会把坏包放过去。脚本改为断言产物本身（exe 必须比引擎模板大，且目录里不能出现 `.pck` 或 `data_*`），再让成品自己跑一次 `--smoke-test` 并把启动行里的资源计数读出来，证明 Pack 里确实带了 CSV 与 JSON。

```powershell
powershell -File tools/PackWindows.ps1 -SkipBuild -SkipChecks -SkipImport   # 只重打包，快速迭代
```

每次会在 `artifacts/pack-preview.png` 留一张打包版的实际画面。**中文界面依赖系统字体 Microsoft YaHei，必须肉眼确认不是豆腐块**——字体缺失是静默的，不会抛异常。目标机器缺字体时的处理见 [资源规范](docs/art/asset_spec.md)。

## 换电脑继续开发

提交并推送整个 `AsmodeusGame` 仓库，包括 `docs/`、`idle-sword/`、`tools/` 和 `tests/`。家中拉取后安装同版本工具，从 [文档索引](docs/README.md) 接续；无需依赖本地会话同步。不要提交 `.godot/`、`bin/`、`obj/`、`artifacts/`。

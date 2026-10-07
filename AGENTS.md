# Idle-Sword 开发约定

- 工作前阅读 `docs/README.md`、`docs/design/core_rules.md` 和 `docs/planning/phase_01.md`。
- **动战斗相关代码或数值前，先读 `docs/design/combat.md`**（伤害公式 `DMG1/DMG2/DMG3`、事件顺序、出手快照、频率分离的权威口径）。新增或改动战斗属性另读 `docs/design/fightattr.md`。伤害公式只许有一份实现：`Core/Data/DamageFormula.cs`，逐拍模拟与 `LevelCurve` 期望模型共用。**期望模型必须算上模拟里实际生效的每一个乘区**——模型漏掉一个乘区就是静默低估，而 `--check` 不会告诉你。
- Godot 工程在 `idle-sword/`，文档在外层 `docs/`。不要另建平行工程。
- 使用 C#；纯逻辑在 `Core/` 和 `Features/`，不得依赖 Godot 节点。表现与输入在 `UI/`。
- 养成操作统一通过 GameSession，界面不得直接扣除货币或发放奖励。
- 灵核只由每个稳定关卡 ID 的首次 BOSS 击杀发放 1 个，重复发放为 0。永久账本与奖励必须作为同一次存档提交。
- 格号不是瞬移指令。连续移动、进入格激活刷怪、越过刷怪点停刷是核心规则。
- 文档和 CSV 采用 UTF-8。指定 CSV 大小写保持一致，C# 类型与文件使用 PascalCase。
- **改文本一律用 Edit 工具，不要用 PowerShell 做字符串替换**：Windows PowerShell 5.1 的 `Get-Content -Raw` 对无 BOM 的 UTF-8 文件按 GBK 解码，`Set-Content -Encoding utf8` 再写回去会把整篇中文变成乱码（`Main.cs` 被这样毁过一次，靠 `git checkout` 才还原）。需要批量变换时用 Edit，或用能显式指定读写编码的工具。
- 公开接口、公式、边界状态和非显然行为写中文注释；修改配置字段同步更新字段字典与校验。
- **凡是给玩家看的"配置 → 文案"都要写清具体效果**（技能 tips、修行节点、参悟、剑灵……同一套要求）：列关键参数与**当前 → 下一级**的实际数值，不能只写风味描述。三条硬要求：① 取值分支**穷举 + 抛错**，不许留含糊兜底（含糊兜底会让"白名单加了取值却忘了写文案"变成一句看起来没问题的错话）；② **不许出现配置里的原始枚举值**（`bonus_vs_state` 这类内部 id 漏到界面上过一次）；③ 实现放在 `Core/` 或 `Features/`（不放 `UI/`），这样自检能直接断言它——参考 `Core/Data/SkillText.cs` 与 `Features/Talent/TalentText.cs`。
- **调整任何数值前先读 `docs/design/balance_ttk.md`**（技能、怪物、关卡倍率、天赋、武器都一样），并按里面的「境界对齐」三步走；不要凭单个数字去改，先回到刻度口径。
- 原型样例规则不得擅自标记为最终设计。美术替换优先修改 `Assets/visuals.json`。
- 修改核心行为后运行 `dotnet run --project tests/IdleSword.Checks.csproj -- idle-sword/Config/Tables`。
- 修改 UI 后编译并运行 Godot `--smoke-test`；布局变更使用 `--capture` 检查实际画面。
- 不提交缓存、生成构建产物、存档或截图。用户未要求时，不自动提交或推送 Git。

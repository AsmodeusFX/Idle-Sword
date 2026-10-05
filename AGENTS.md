# Idle-Sword 开发约定

- 工作前阅读 `docs/README.md`、`docs/design/core_rules.md` 和 `docs/planning/phase_01.md`。
- Godot 工程在 `idle-sword/`，文档在外层 `docs/`。不要另建平行工程。
- 使用 C#；纯逻辑在 `Core/` 和 `Features/`，不得依赖 Godot 节点。表现与输入在 `UI/`。
- 养成操作统一通过 GameSession，界面不得直接扣除货币或发放奖励。
- 灵核只由每个稳定关卡 ID 的首次 BOSS 击杀发放 1 个，重复发放为 0。永久账本与奖励必须作为同一次存档提交。
- 格号不是瞬移指令。连续移动、进入格激活刷怪、越过刷怪点停刷是核心规则。
- 文档和 CSV 采用 UTF-8。指定 CSV 大小写保持一致，C# 类型与文件使用 PascalCase。
- 公开接口、公式、边界状态和非显然行为写中文注释；修改配置字段同步更新字段字典与校验。
- **调整任何数值前先读 `docs/design/balance_ttk.md`**（技能、怪物、关卡倍率、天赋、武器都一样），并按里面的「境界对齐」三步走；不要凭单个数字去改，先回到刻度口径。
- 原型样例规则不得擅自标记为最终设计。美术替换优先修改 `Assets/visuals.json`。
- 修改核心行为后运行 `dotnet run --project tests/IdleSword.Checks.csproj -- idle-sword/Config/Tables`。
- 修改 UI 后编译并运行 Godot `--smoke-test`；布局变更使用 `--capture` 检查实际画面。
- 不提交缓存、生成构建产物、存档或截图。用户未要求时，不自动提交或推送 Git。

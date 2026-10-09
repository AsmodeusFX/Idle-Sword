
# 《土豆修仙》战斗评估系统方案 V1.0

> **状态（2026-10-08）：已按工程口径对账改写。** 本文原是与 AI 讨论产出的方案，**脱离工程撰写**，
> 与既有战斗规则有 9 处冲突（4 处架构级）。现已按下面三条拍板口径改写到与本工程一致；
> 逐条对账与处置见文末「**与《战斗底层规则》的差异**」（≈ [combat.md](../design/combat.md) §11
> 与 [fightattr.md](../design/fightattr.md) §6 的同一手法）。
>
> | 拍板项 | 取值 |
> | --- | --- |
> | 排期 | **降规模，随数值批次走** —— 现在只做「骨架 + 一致性对账」；属性价值表 / Equivalent Value / BD 排行等数值批次时再做 |
> | 主指标 | **SU / TTK 为纲**（[balance_ttk.md](../design/balance_ttk.md)），60s DPS 降为工具内部的场景指标 |
> | 实现形态 | **`tools/` 下的 harness，复用 `GameSession` + `SandboxMode`** —— 不造平行引擎、不写 EventQueue |
>
> **为什么降规模而不是推迟**：搭「量化评估的能力」不依赖内容定形，依赖「复用正式战斗规则」这条架构约束；
> 而在这个工程里它的引擎**几乎是免费的**（§4）。会作废的是**价值表的数值** —— 本文 §38 自己说了
> "属性价值依赖当前 Build、技能结构、Buff、Effect 和战斗场景"，而不是工具本身。
>
> **必须提防的失败模式：造出第二把尺子。** 工程已有一把跨三处共用的尺子：刻度锚点
> `SU = 3 × 裸攻击 = 30`、期望模型 `Core/Data/LevelCurve.cs`、唯一公式实现 `Core/Data/DamageFormula.cs`、
> 记账契约 `DamageStats`。把 60s DPS 定为主指标会与它**并列** —— 那正是
> [doc_audit.md](../design/doc_audit.md) A2 记的「同一份文件里并存两套刻度」那类病，只是这次跨工具。

## 1. 系统定位

**战斗探针（内核 `Features/Battle/CombatProbe.cs`，出口是关卡编辑器的只读节「战斗探针」）**
用于对《土豆修仙》的角色、技能、Effect、Buff、属性以及 BD 进行自动化战斗能力评估。

> **名字刻意不叫 "DPS"**：主指标是 **SU 与 TTK**（[balance_ttk.md](../design/balance_ttk.md) 的刻度口径），
> DPS 只是工具内部的一个场景指标。叫 DPS 会把"另立一把尺子"的框架带回来。

系统不是独立的“DPS 公式计算器”。

而是：

> **复用正式战斗系统规则，在无画面表现层的情况下，执行一场可重复、可统计的虚拟战斗。**

核心目标：

1. 评估角色持续输出能力。
2. 评估角色爆发输出能力。
3. 评估不同属性对 **SU / TTK** 的边际价值（额度本身留到内容定形后，见 §60）。
4. 评估 Skill / Effect / Buff 的实际战斗价值。
5. 评估不同 BD 的输出差异。
6. 发现异常的技能循环、触发链、伤害倍率。
7. 为后续数值平衡提供可量化依据。

> ⚠️ **第 7 条要配上经济侧才成立。** 工程的关卡强度是**经济模拟**推出来的
> （`LevelCurve`：每关收入 → 沿修行树贪心买最便宜的一档 → 受灵核门封顶）。
> 单靠 DPS 答不了"这一关强度够不够" —— 还得知道"这一关买得起多少练度"。
> 两者必须一起算，否则会得出"DPS 达标"而玩家根本点不起天赋的结论。见 §61。

---

# 2. 核心原则

## 2.1 不重复实现战斗规则

战斗探针不允许自己重新实现：

- DMG1
- DMG2
- DMG3
- Crit
- DamageModifier
- Skill
- Effect
- Buff

这些规则必须复用正式战斗系统。

目标：

```text
  正式战斗（游戏内）          CombatProbe（工具）
        │                          │
        └────────┬─────────────────┘
                 ↓
        GameSession + Battle（同一份）
                 ↓
        Core/Data/DamageFormula.cs（唯一公式实现）
```

而不是：

```text
正式战斗
        ↓
一套规则

CombatProbe
        ↓
另一套简化规则
```

必须保证：

> **同一个角色、同一个技能、同一个战斗状态，在正式战斗和 Simulator 中产生相同的战斗结果。**

> ✅ **本节是整份文档最有价值的一条，原样保留。**
>
> ⚠️ **但要划清"重复实现"的边界 —— 它禁的是"公式的第二份实现"，不是"第二个算路"。**
>
> 工程是**刻意**有两条算路的：逐拍模拟（权威行为）+ 期望模型 `LevelCurve`（快、稳、可搜索）。
> 它们**共用同一个 `DamageFormula.cs`**，并由自检钉住一致性。见 §35.1。
>
> 所以本节的准确说法是：
>
> | 允许 | 不允许 |
> | 两条算路共用同一个 `DamageFormula.cs` | 探针自己写一份伤害公式 |
> | `LevelCurve` 用解析式算期望 | `LevelCurve` 自己发明一套暴击规则 |
> | 探针驱动 `GameSession` | 探针自己写一套技能释放 / 目标选择 / 命中判定 |
>
> 一句判据：**"这条规则在正式战斗里改一行，探针要不要跟着改？"**
> 答案是"要" ⇒ 那是重复实现，必须改成复用。
> 答案是"不用，因为它调用的是同一个函数" ⇒ 那是合法算路。

---

# 3. 模拟器不模拟什么

V1 明确不模拟表现层。

不模拟：

- 动画
- 角色移动
- 怪物移动
- 实际碰撞
- 投射物飞行时间
- 摄像机
- 特效表现
- 音效
- 网络延迟
- 帧同步
- UI

除非某个机制已经被正式战斗逻辑定义为“战斗事件”，否则不进入 Simulator。

例如：

```text
飞剑需要飞行 0.5 秒
```

如果正式战斗系统未来定义：

```text
SpawnProjectile
→ 0.5s 后 Hit
```

那么 Simulator 可以模拟这个 0.5 秒事件。

但不需要模拟：

```text
飞剑在屏幕上飞行
```

> 🚨 **改写：投射物飞行时间在工程里不是"未来"，它已经是一个战斗事件 —— 而且它决定谁挨打。**
>
> | 文档原本以为 | 工程实际 |
> | "飞剑在屏幕上飞行"不模拟 | **"飞行导致命中时机推迟"必须模拟** |
>
> 依据：`CombatEffect` 带 `Timer` + `Trajectory` + `speed`；
> 弹丸在**到达时刻**才做命中判定，中途目标死了 / 走出了范围，这次出手就落空。
> 换句话说 **飞行时间不是表现，是目标解析的一部分** ——
> 排除它等于假设所有攻击瞬时到达，于是"打移动靶"的一切结论都会偏乐观。
>
> 所以正确的切分是：
>
> | 排除（真的只是表现） | 保留（已经是战斗事件） |
> | 飞剑的**贴图**在屏幕上飞 | 飞剑**到达的时刻** |
> | 飞行途中的**拖尾特效** | 到达时**目标还在不在**（是否已死 / 是否已出范围） |
> | 音效 | `speed` 决定的**弹道延迟** |
>
> 探针用 `SandboxMode` 跑时靶子定身放远（§18），弹道延迟依然生效、依然影响 DPS ——
> 这一点容易被"靶子不动所以无所谓"的直觉漏掉。

---

# 4. 核心模拟模型

采用：

> **固定步长逐拍模拟，直接跑正式会话。**

**不是离散事件模拟（Discrete Event Simulation）。** 这是改写前后最大的一处差别，理由不是
"哪种更先进"，而是：**`EventQueue` 本身就是战斗推进规则的第二份实现** —— 它直接违反本文 §2.1
「不重复实现战斗规则」。一旦有两套推进方式，"什么时候结算"就会分叉，而**两边都不会报错**。

工程的战斗会话 `Features/Battle/GameSession.cs` 已经是固定步长（`fixed_step`）逐拍推进的，
而且有一个现成的模式正对这件事：

> **`GameSession.SandboxMode`**（`GameSession.cs:156`）

它跳过 **移动 / 激活格子 / 刷怪 / 玩家死亡 / 通关 / 落盘**，
而 **冷却流逝、施法、效果推进、命中结算、状态计时、敌人死亡与移除照常**
（`GameSession.cs:247` 的 `RemoveAll(e => e.Hp <= 0)` 在沙盒里照跑）。

所以探针的形态是 **自己摆场景 → 推进正式会话 → 收统计**，
而不是"另写一个引擎、再想办法证明它和正式战斗同结果"。

参照实现：`UI/SkillPreview.cs:120`（GM 技能预览）已经在用这条路 ——
自己的会话实例、自己的战斗视口、世界静止、沙盒不落盘。

> **为什么"世界不动"必须是会话自己的保证**：技能预览从前靠 UI 侧打补丁
> （关普攻 + 把当前格标 `Passed` + 每步重钉靶子），被"近战停步 120 而靶子钉在 520"
> 这种**正规改动的连带影响**击穿过一次 —— 角色一路前进、走进新格就激活新刷怪点、当拍刷怪。
> 探针同理：不要在 harness 里对抗 `Step` 的每一个推进源。

---

# 5. 基本结构

```text
CombatProbe（Features/Battle/CombatProbe.cs）
    │
    ├── Scenario      PlanWaveField(order)：按这一关**真实的**波次表摆靶（数量走 WaveCountsFor、分层走 monster.layer）
    │
    ├── Session       GameSession（SandboxMode = true）—— 规则全在这里
    │
    ├── Runner        推进循环：Step(fixed_step=50ms) → 补靶 → 采样统计（Report 只是它的调用方）
    │
    ├── Statistics    在 DamageStats 之上分组（不另立口径，见 §58）
    │
    └── Report        SU / TTK 为主指标的输出（屏上：关卡编辑器「战斗探针」节）
```

**没有 `EventQueue`、没有 `CombatSystem` 的副本、没有 `DamageSystem` 的副本** ——
它们就是 `GameSession` 与 `Core/Data/DamageFormula.cs`。

`Runner` 负责一件 `SandboxMode` **不负责**的事：**补靶**。
沙盒保证的是"世界不动"，**不是"靶子常驻"**（见 §18）。

---

# 6. 推进循环

```text
while (Session.Elapsed < Duration)
    ├── Session.Step(fixed_step)
    ├── 结算玩家承伤（沙盒不结算死亡，见 §17）
    ├── 补靶：目标被打死后重新摆位（§18）
    └── 采样统计
```

- **固定步长与正式战斗同一个值**（`GameSession` 的 `fixed_step`），不另取一个 ——
  步长会改变状态结算的粒度（灼烧每步一跳、弹丸每步走一段），换一个值就不是"同一场战斗"了。
- 推进按**模拟时钟**（`Session.Elapsed`）而不是真实帧数。工程踩过这个坑：
  同一份代码在有窗口 / 无窗口下模拟速度差十倍，断言会变成抽签。
- 终止条件：`Elapsed >= Duration`，或（有限血场景下）目标清空且补靶策略设为"用尽即停"。

---

# 7. 事件类型

**工程没有事件总线，也没有 `OnHit` / `OnKill` 钩子。** 已经落地的只有很小的一块：
`SkillBuffTrigger.csv` 表驱动（`trigger_type` 白名单里**只有 `on_crit` 与 `on_skill_cast`**），
见 [combat.md](../design/combat.md) §8。

所以：

- 本方案的"事件"**不是要新造一套枚举**，而是**给逐拍模拟加一层只读观测** ——
  从 `GameSession` 的公开状态采样（`Effects`、`EnemyState`、`Battle.Cooldowns`、`DamageStats`），
  在 harness 里聚合成时间线。**观测一律不许回写任何玩法状态**（这是技能预览与属性面板的同一条契约）。
- **不许**把 `on_hit` / `on_kill` / `on_damage` / `on_enemy_death` 填进白名单 ——
  那是"配置上写了、战斗里没人读、且不报任何错"的永不执行分支。
  链式事件要真做，先把 [combat.md](../design/combat.md) §8 的事件总线建起来。
- 需要新事件类型时（例如"额外飞剑"），正确做法是**在玩法里产生新的 Attack Event**，
  而不是在 harness 里编一条。

---

# 8. Skill 模拟

Skill 仍然遵循正式 Skill 结构。

Simulator 不直接“计算技能伤害”。

Skill 只负责：

```text
是否可以释放
↓
目标选择
↓
开始释放
↓
释放完成
↓
触发 Effect
↓
触发 Buff
↓
进入 CD
```

例如：

```text
Skill
    ↓
Check Cast
    ↓
Target Resolution
    ↓
Cast
    ↓
Effect
    ↓
Buff
    ↓
Cooldown
```

---

# 9. 技能释放规则

> ⚠️ **改写：本节原来是 "Ready Skill Priority"（多技能就绪时按优先级选一个）。**
> 那**不是这款游戏的规则**。工程的释放规则如下，探针照原样跑，不额外定义策略。

1. **法术没有优先级、彼此不竞争**：冷却归零就释放（`GameSession.CastSkills`），
   有几个就放几个，不存在"选了 A 就不能放 B"。
2. **普攻不是"没有法术可放时的兜底"** —— 它按 `fightattr.basic_interval`（1 秒）
   **与法术并行**跑（`TickBasicAttack`）。把它写成兜底会把普攻频率压成"法术空隙"，直接算错。
3. **神通（`trigger_chance > 0`）不自动释放**：它只在**普攻出手的那一刻**、在冷却就绪的前提下
   按概率触发一次并重置冷却。当前唯一在役的是御雷真诀（`trigger_chance = 0.3`）。
   所以它不是循环里的一环，而是**普攻的派生物**。
4. **攻速只加速输出类法术与剑灵，不加速增益类法术**（见 §12）。

> 这一条决定了探针**不需要"技能 AI"**：引擎里没有要模拟的决策。所有"什么时候出手"
> 都是配置与冷却的函数。

---

# 10. 关于「BD 循环对比」

> ⚠️ **改写：本节原来要求 `SimulationConfig.skill_priority` 可配置，用来比较"循环 A / B / C"。**
> 工程里**没有这个配置位**，因为**游戏里没有这个决策** —— 法术冷却好就放（§9）。

要做"同一个角色跑不同循环"的对比，有两条路，**都不是探针能自己决定的**：

| 路 | 代价 |
| --- | --- |
| **在玩法里引入优先级**（法术也得竞争出手权） | 那是**改玩法**，不是加工具。会连带改动 `CastSkills`、技能期望模型（`LevelCurve.Cycle`）与既有断言 |
| **只比较不同的练度 / 技能组合**（换 build 而不是换循环） | 探针现在就能做 —— 这也是 §48「Build Comparison」的真实含义 |

> **V1 采用第二条**：探针比较"不同 Build / 练度点 / 场景"，**不比较"同一 build 的不同循环"**。
> 这一条要在报表上写清楚，否则用户会以为"循环"是可调参数。

---

# 11. 基础攻击

> ⚠️ **改写：原来写的是"如果没有更高优先级 Skill Ready → 执行 Basic Attack"。**
> 普攻**不是法术的兜底**，它与法术**并行**按自己的间隔跑（§9 第 2 条）。

规则（与 `combat.md` §5 同口径）：

```text
普攻间隔 = fightattr.basic_interval ÷ (1 + attack_speed)
```

- **用除法**，所以再大也不会出现零间隔。
- **夹在 `fightattr.max_value`**（`attack_speed` 上限 **3**），夹的是**合计值**
  （基础 + 各来源），所以增益也吃同一条上限。
- 普攻的 `SkillRate` 就是 `fightattr.basic_power`（`combat.md` §2）。

**形态差异不影响结算**：开局是近战、点了「剑气」转远程，改的只是 `melee_*` / `basic_*`
那两组射程与停步距离 —— 出手本身走的**同一条 `Launch("projectile", …)`**。
纯输出场景里两者等价；只有"够不够得着"的场景才要区分。

> ⚠️ **普攻照常吃暴击伤害，但不触发「暴击缩短技能冷却」**（那条只认法术，见 §12）。

---

# 12. Skill CDR

Skill CDR 只影响技能释放频率。

不直接进入 Damage 公式。

例如：

```text
Base CD = 10s
Skill CDR = 20%
```

得到：

```text
Runtime CD = 8s
```

但不会：

```text
Damage × 1.2
```

✅ 这一条与工程一致（`combat.md` §5「频率与伤害分离」）。

> ⚠️ **补两条工程规则，原文档没写，漏掉会让增益类法术在模型里被算成常驻：**
>
> 1. **增益类法术（`buff`）既不吃 `attack_speed` 也不吃 `skill_cdr`。**
>    它的冷却就是 `SkillEffect.cooldown` 原值。原因很直白：一个 15 秒冷却、6 秒持续时间的增益，
>    一旦吃满 CDR 就会变成 100% 覆盖 —— 那不是"模型精度"问题，是**模型造出了一个游戏里不存在的状态**。
>    所以探针在按法术分类汇总时，必须把 `buff` 类单独列，不能和输出类混在一张 CDR 表里。
> 2. **「暴击缩短冷却」（`crit_reduce`）按秒扣，且服从下限。**
>    它不是乘倍率，是从当前冷却里**减掉一个秒数**；但任何一次缩短都不得把冷却压到
>    `该法术冷却 ÷ (1 + skill_cdr 上限)` 以下（`skill_cdr` 上限 **1**，即最多减半）。
>    没有这条下限，高暴击 build 会把长冷却法术刷成无冷却 —— 探针会把 bug 当成构建强度量出来。

---

# 13. 战斗事件链

> 🚨 **改写：原文档把这条链画成"Skill → … → DMG1 → DMG2 → DMG3 → …"，读起来像三级乘法。
> 但 DMG2 不是倍率，它是事件顺序** —— `combat.md` §3 的九步瀑布。
> 把 DMG2 画在 DMG1 与 DMG3 之间当一级放大，是这份文档最容易被照抄进代码的错误。

工程的权威链条（`combat.md` §3，逐字对齐）：

```text
① Attack Event        —— 出手，按当前值做「出手快照」（§3.4）
② Target Resolution   —— 选定目标（含层规则、距离、聚怪）
③ Hit Resolution      —— 命中判定（闪避在这里）
④ Damage Event        —— 名义伤害成立
⑤ DMG3                —— 乘区结算（§4）
⑥ Apply Damage        —— 落血，记 damage/effective/overflow
⑦ OnDamage / OnHit / OnCrit
⑧ Death → OnKill
⑨ Chain / Trigger     —— 未实现（§8）
```

关键在 **③ 与 ⑤ 是两步**：

- **暴击的"判定"发生在事件链里（③ 一带），"倍率"发生在 DMG3（⑤）。**
  写作 `Crit = true` 与 `DMG3 = ×1.8` 并列（原 §53 的事件日志示例就是这么写的）会让人以为暴击是一次乘法，
  于是漏掉「出手快照」这条工程规则。
- **出手快照（`combat.md` §3.4）**：一次 Attack Event 在**发生时**把攻击方侧的 DMG1、暴击结果、
  暴击倍率、共享伤害窗口全部快照下来；该次出手派生的所有结算**共用这份快照，不再重掷**。
  这条不是优化，是**语义** —— 探针若不遵守，同一次多段攻击的各段会各自摇暴击，输出会系统性偏离。

⑨ 整步**未实现**（`combat.md` §8）：工程里没有事件总线、没有 `OnHit`/`OnKill` 钩子、
没有 `DamageModifier` 注册表，已落地的只有 `SkillBuffTrigger.csv`（白名单 `on_crit` / `on_skill_cast`）。
探针**不得**为它预留"能跑但永远不触发"的分支，也不得把 `on_hit` / `on_kill` 提前填进白名单。

> ⚠️ 原文档写的"任何 Trigger 产生新的 AttackEvent 都重新进入统一 CombatSystem""禁止直接增加 DPS"
> **原则完全正确**，但在 V1 是**空条款** —— 因为还没有任何 Trigger 能产生新的 AttackEvent。
> 保留它作为纪律，别把它当成待测功能。

---

# 14. Damage 计算

Simulator 必须调用正式 `DamageSystem`。

> 🚨 **改写：算式要补一个前提 —— DMG2 不是乘区。**
> 下面前两段（DMG1、DMG3）与工程一致，第三段"最终伤害"的部分**必须按 `combat.md` §0/§4 分组**，
> 不能写成"DMG1 × 四个乘区平铺"，那等于把 DMG2 抹掉了。

DMG1（`combat.md` §2）：

```text
DMG1 = AttackPower × SkillRate + SkillFlat
AttackPower = (atk_base + 武器攻击) × (1 + atk_percent + 天赋百分比) + 天赋平攻
```

DMG3（`combat.md` §4）—— **这四个才是"乘区"**：

```text
DMG3 = GenericMultiplier
     × CriticalMultiplier
     × BuildMultiplier
     × VulnerabilityMultiplier
```

最终伤害（`combat.md` §0 的一句话权威口径）：

```text
最终伤害 = DMG1 × Generic × Critical × Build × Vulnerability
```

**DMG2 去哪了？** —— 它不在这条乘法链里，它是**上面 §13 的九步事件顺序**。
把 DMG2 写成乘区，就是本工程最忌讳的"第二把尺子"：同一次伤害会有两处算法。

Simulator 不允许创建第二套 Damage Formula —— **只调用 `Core/Data/DamageFormula.cs`**
（`AGENTS.md`：伤害公式只许有一份实现，逐拍模拟与 `LevelCurve` 期望模型共用）。

---

# 15. Buff 模拟

Buff 必须使用正式 Buff 生命周期。

包括：

```text
Apply
↓
Duration
↓
Timeline
↓
Trigger
↓
Stack
↓
Refresh
↓
Interrupt
↓
Remove
```

Simulator 必须记录：

```text
Buff Apply Count
Buff Remove Count
Buff Uptime
Buff Trigger Count
Max Stack
Average Stack
```

> ✅ 生命周期与记录项都成立。**只是"Buff Trigger Count"要按白名单理解**：
> 工程的触发白名单只有 `on_crit` 与 `on_skill_cast`（`SkillBuffTrigger.csv`），
> 所以这一项**只统计这两种**，不要给它加 `on_hit` / `on_kill` 之类的分支（§13）。
>
> ⚠️ **`Max Stack` / `Average Stack` 要区分"叠层"与"刷新"**：
> 有些 buff 是加层数（越打越强），有些只是刷新时长（强度不变）。
> 把后者按前者统计，会得出"平均层数 = 1，看起来没生效"的假象 ——
> 报表上应当把两类分开，或者至少标注该 buff 是"叠层型 / 刷新型"。

---

# 16. Effect 模拟

Effect 是最终行为执行单元。

Simulator 应直接调用：

```text
ExecuteEffect()
```

Effect 可以产生：

```text
Damage
Heal
Projectile
ApplyBuff
RemoveBuff
Knockback
TriggerSkill
RefreshCooldown
```

以及其他正式系统定义的行为。

> ⚠️ **改写：上面这份清单是"设想"，不是工程的 `effect_type` 白名单。**
> 探针只能按**白名单里实际存在的取值**分派，并且**穷举 + 抛错**（`AGENTS.md` 对"配置 → 文案"的同一套硬要求）：
> 白名单加了新取值而探针没跟上，必须**报错**，不许落进"其它"分支静默略过 ——
> 含糊兜底会让一个没模拟的 effect 在报表上表现为 0 贡献，看起来像"这个效果没用"。

任何 Effect 产生的新战斗行为，都**按 §6 的推进循环重新进入同一会话**，不走队列。

汇总时按 `effect_type` 分组，**不做语义归并** —— 例如"多段"与"弹射"是两条独立路径，
即使它们都可能命中同一目标，也各算各的（与 §19 的多目标口径一致）。

---

# 17. 无限 HP 标准目标

第一种标准场景：

> **Single Target Infinite HP**

配置：

```text
Target Count = 1
Target HP = Infinite
Target Defense = 按正式系统规则
Target State = Normal
```

如果 V1 没有防御体系：

```text
不添加任何额外防御计算。
```

这个模式用于：

> **纯理论 DPS 和属性价值评估。**

> 🚨 **补一条原文档漏掉的重大限制：无限血会把三种最有价值的 Build 倍率量成 0。**
>
> 工程里有几条判定依赖"目标当前血量"：
>
> | 机制 | 依赖 | 无限血下的表现 |
> | --- | --- | --- |
> | **斩杀（`execute`）** | 目标气血**比例**低于阈值才触发 | 比例**恒为 1**，**永不触发** → 量成 0 |
> | **利用状态**（`bonus_vs_state`） | 目标须处于某状态（如已受伤 / 被控） | 状态若由"掉血"派生则**永不满足** → 量成 0 |
> | **易伤（`vulnerability`）** | 通常由"命中后"或"低血"施加 | 施加条件若含血量则**永不满足** → 量成 0 |
>
> 于是：**无限血场景下，一个靠斩杀吃饭的 build 与一个不靠斩杀的 build 会显示成同样的 DPS。**
> 这不是精度问题，是**这个场景根本不测那三样东西**。
>
> **处置**：无限血模式**只用于"纯输出频率与基础乘区"的横向比较**；
> 任何涉及上述三类的结论，必须换到 §18 的有限血（且血量台阶要与关卡真实血量同量级）。
> 探针报表必须在"无限血"场景的标题上**显式标注"斩杀 / 利用状态 / 易伤在本场景失效"**，
> 否则用户会拿这张表去调数值。

---

# 18. 有限 HP 标准目标

第二种模式：

```text
Target HP = Fixed
```

例如：

```text
10,000
100,000
1,000,000
10,000,000
```

用于测试：

- TTK
- Overkill
- 技能循环
- 目标死亡触发
- OnKill
- 切换目标
- 实际战斗效率

> ⚠️ **三点对账：**
>
> 1. **"Overkill" 在工程里叫「溢出」（overflow）**，是 `DamageStats` 的既有口径之一
>    （有效 = 实际打掉的血 / 溢出 = 名义伤害超出剩余血量的部分 / 命中 = 落血次数）。
>    探针**必须对齐这个口径**，不要另起一套。见 §25 与其后 §26 的处置。
> 2. **"OnKill" 目前没有实现**（`combat.md` §8：链式事件整步未实现）。
>    这一条**测不出东西**，V1 不要为它写断言 —— 写了就是空断言，永远绿。
>    能测的是"**目标死亡之后发生了什么行为**"（换目标、重新摆位），那属于会话的推进循环。
> 3. **演练场的目标补充由 harness 负责。** `SandboxMode` 的保证是"世界不动"
>    （跳过移动 / 刷怪 / 玩家死亡 / 通关 / 落盘），**不是"靶子常驻"** ——
>    敌人在死亡的那一次 Step 内就被移除。所以有限血场景里，敌人倒下后
>    **必须由探针重新摆位**，否则目标清空、玩家不出手、模拟空转（而报表看起来只是"数字低"）。
>    另外靶子要**定身且放远**：不定身怪物会朝玩家走、弹道到达时机随之漂移；
>    贴脸摆的话多发弹丸会在**同一个 Step 内**命中并被移除，编排根本数不出来。

---

# 19. 多目标模式

V1 支持：

```text
Target Count = 1
Target Count = 3
Target Count = 5
```

不允许简单：

```text
SingleTargetDPS × TargetCount
```

必须真正运行目标选择和 Effect 逻辑。

例如：

```text
单体技能
范围技能
随机目标
弹射
AOE
多段
```

都按照正式 Target Resolution 执行。

> ⚠️ **补：`Target Count` 一个参数不足以描述多目标场景。** 原文档只列了目标数，
> 但工程里"打中几只"由**四个**各自独立的因素决定，任何一个变了结果都变：
>
> | 参数 | 作用 |
> | --- | --- |
> | **目标数** | 场上有几只 |
> | **层构成** | 招式带 `hits`（能打到哪一层：地面 / 空中）对 `monster.layer` —— **打不到层就是 0**，不是"少一点" |
> | **间距（`spread`）** | 散射 / 范围招能覆盖几只；间距是连续量，不是"密集/稀疏"两档 |
> | **是否聚怪** | 决定上面的间距取小值还是大值 |
>
> 另外 **`targeting = highest_hp`（挑血最多的）与 `aoe_all`（打全体）是两条独立路径**，
> 不能因为"都是范围"就归成一类汇总。这几项**必须进 Scenario 参数**（§50），
> 并在报表上**随结果一起打印** —— 否则两次跑出来的 SU 不一样，没人知道是数值变了还是摆位变了。

> ✅ **原文档警告的"不允许 `SingleTargetDPS × TargetCount`"完全正确，且工程已有一条更强的检验**：
> 探针的健全性检查里应当有"**3 目标场景的 SU 不得恰好等于单目标的 3 倍**"——
> `line_pierce` / `aoe_all` / 聚怪的收益各不相同，恰好相等通常意味着效应被抹平了。

---

# 20. 标准时间窗口

所有标准评估默认输出：

```text
1s
3s
5s
10s
30s
60s
120s
```

其中：

> 🚨 **改写：原文档说"60 秒 Sustained DPS 为主指标"。本工程的主指标是 SU / TTK。**
>
> 这不是措辞问题。工程已经有一把**跨三处共用**的尺子：
> `SU = 3 × 裸攻击 = 30`（`LevelCurve`）、期望模型、唯一公式实现 `DamageFormula`、
> 记账契约 `DamageStats`。把 60s DPS 抬成主指标，就是**在同一套工具里并列第二把尺子** ——
> 正是 `doc_audit.md` A2 记的"同一份文件里并存两套刻度"那类病，只是这次跨了工具。
>
> **口径（已拍板）**：
>
> | 层级 | 指标 |
> | **纲** | **SU（标准单位）与 TTK** —— 对外的一切结论、关卡强度、成长预期，都换算到这两个上 |
> | **工具内部场景指标** | 1s/3s/…/120s 各窗口 DPS、Burst、伤害拆解 —— **允许存在，但必须能与 SU 互推** |
>
> **互推的桥是确定的**：`combat.md` §5 的期望 DPS 公式给出"每秒几个 SU"，
> 所以 `N 秒 DPS → N 秒内打掉多少 SU` 是恒等变换，不是近似。
> 报表里两个数**必须同时出现**，且换算关系写在表头 ——
> 只给 DPS 不给 SU，用户就没法把它接到关卡强度上（而关卡强度正是这把工具存在的理由之一）。

---

# 21. DPS 定义

```text
DPS =
Total Effective Damage / Simulation Duration
```

✅ 定义式与工程一致，但要**换成工程的名字**：

| 原文档 | 工程（`damage_stats_contract`） |
| `Raw Damage` | **名义伤害**（Damage Event 成立时的值，未落血前） |
| `Effective Damage` | **有效伤害**（实际打掉的血） |
| `Overkill` | **溢出**（名义 − 有效） |
| —— | **命中次数**（`damage <= 0` 的那次**不进账**） |

对无限 HP 目标：

```text
有效伤害 = 名义伤害（目标永不死亡 ⇒ 溢出恒为 0）
```

对有限 HP 目标：

```text
有效伤害 = 实际减少目标 HP 的伤害
```

> ⚠️ 分母是**模拟时长**不是"战斗时长"：多目标场景里某个目标可能早早死亡、
> 之后只有其余目标在挨打，这个平均值天然会被稀释。多目标结论一律看 SU/TTK（§19），不要看平均 DPS。

---

# 22. Burst DPS

分别计算：

```text
1s DPS
3s DPS
5s DPS
10s DPS
```

用于评价：

> 技能爆发能力。

---

# 23. Sustained DPS

计算：

```text
30s DPS
60s DPS
120s DPS
```

其中：

```text
60s DPS
```

✅ 作为**长期输出的观察窗口**保留。

> ⚠️ **但它是场景指标、不是主指标**（见 §20）。长期输出的"纲"是
> **稳态 SU/秒** —— 即把 60s DPS 换算成"每分钟打掉几个标准怪"。
> 这两个数在报表上**必须成对出现**。

---

# 24. TTK

有限 HP 场景下：

```text
TTK =
Target HP 从初始值下降到 0 的时间
```

输出：

```text
TTK 10K
TTK 100K
TTK 1M
TTK 10M
```

如果在 Simulation Duration 内无法击杀：

```text
TTK = N/A
```

> 🚨 **改写：TTK 是主指标之一，但它的"刻度"必须挂到 SU 上，否则这组绝对值没有意义。**
>
> 工程里关卡强度按 **SU 台阶**排（`LevelCurve`：标准怪 = 1 SU = 30 HP，
> 精英 = 10 SU，BOSS = 75 SU），且**TTK 按构造保持恒定** ——
> 也就是说"同样的练度打同一档强度的怪，花的时间是一样的"，这是整个成长曲线的支点。
>
> 所以探针输出 TTK 时，**必须同时输出"这个靶子值多少 SU"**，
> 并且**以 `LevelCurve` 的真实血量台阶为默认靶子**（而不是原文档随手写的 10K/100K/1M/10M）。
> 自由血量仍然允许，但要标成"自定义"，不要混进标准场景（§50）。
>
> **验收含义**：如果某个练度点上，探针跑出来的 TTK 与 `LevelCurve` 的期望 TTK
> 不成比例地偏离（同量级以外），那是**模型与模拟对不上**，必须先查因为什么，再谈数值。

---

# 25. Effective Damage 与 Raw Damage

必须同时记录：

### Raw Damage

所有 DamageEvent 的理论最终伤害。

### Effective Damage

实际作用到目标 HP 的伤害。

例如：

```text
Target HP = 1000
Attack Damage = 1500
```

记录（工程口径，`DamageStats` 契约）：

```text
名义伤害 = 1500
有效伤害 = 1000
溢出     = 500
命中次数 = 1
```

> ✅ **§25 的定义与工程完全一致，原样保留。** 唯一要统一的是术语：
> 工程里叫**名义 / 有效 / 溢出 / 命中次数**，探针的输出字段名照抄，
> 不要另造 `Raw` / `Effective` / `Overkill` 三个新名字——
> 同一件事两套名字，就是 §2.1 警告的那种分裂的最轻版本，但会一路传到报表和 UI。

---

# 26. Overkill（本章删除）

> 🚨 **本章删除，理由：它与 §25 自相矛盾，且描述的场景在工程里结构上不可能发生。**
>
> 原文写：
>
> > `Overkill = Raw Damage - Effective Damage`，**但只统计"对已经死亡目标继续产生的无效伤害"**。
>
> 两句互相打脸：§25 的溢出是**同一次攻击**里超出的那部分（打 1000 血的目标、打出 1500，
> 溢出 500）；§26 却说要统计"对**已经死亡**的目标继续打"的部分。**这是两件事。**
>
> 而后者在本工程里**不存在**：敌人死亡后，在**同一次 `Step` 内**就被移出战斗
> （`GameSession.cs:247` 的 `RemoveAll(e => e.Hp <= 0)`，沙盒模式下同样照跑）。
> 从死亡到移除之间没有可插入的推进，**没有任何一次攻击能落到一具尸体上**。
> 所以"对已死目标继续产生伤害"是一个**模拟不出来的数** —— 写进去只会得到一个恒为 0 的指标，
> 看起来像"爆发没有浪费"，从而让评估结论偏乐观。
>
> **实际应当统计的"浪费"有三种，都不是 §26 说的那种**，各自的量也在别处：
>
> | 浪费 | 在哪量 |
> | 单体爆发打小怪（一炮打 3000，怪只有 300 血） | **溢出**（§25） |
> | 多段攻击的后几段落空 / 打在已死目标以外 | **命中次数**与**有效伤害**的比例 |
> | 目标选择低效（该打残血却打了满血） | **TTK** 与**换目标次数**（§19/§24） |
>
> 这三样用 §25 的既有口径 + §24 的 TTK 就够了，**不需要独立的一章**。
> 填报表时"溢出率 = 溢出 ÷ 名义伤害"直接给出 §26 想要的那个洞察。

---

# 27. Damage Breakdown

每次模拟必须记录伤害来源。

至少按照：

```text
Basic Attack
Skill
Effect
Buff
Trigger
```

分类。

例如：

```text
Total Damage = 1,200,000

Basic Attack      320,000
Sword Control     420,000
Sword Intent      180,000
Bleed             120,000
Other             160,000
```

同时记录百分比：

```text
Damage Share
```

> ⚠️ **与工程记账契约对齐（`DamageStats`）：**
>
> - **归因到法术 id**；**普攻用空串**（不是 `"Basic Attack"` 这个字符串）；
>   **影分身归到复制它的那个法术名下** —— 不是单独一类。
> - **`Trigger` 这一类现在恒为空**（链式事件未实现，§13）。可以留着这一行占位，
>   但报表上必须显示"未启用"，不能显示 `Trigger 0 damage` —— 后者会被读成"触发收益为零"。
> - **`Other` 是危险字段**：它把"漏归因"和"真的小来源"混在一起。
>   探针应当**穷举**已知来源并**抛错**，而不是留一个 `Other` 兜底
>   （`AGENTS.md` 对"配置 → 文案"的同一套要求：含糊兜底会把遗漏伪装成正常）。
> - **`Buff` 这一类要小心重复计数**：持续伤害（如流血）既可能归到施加它的法术，
>   也可能被算成 buff 自身的贡献。工程口径是**按伤害事件的实际归因算一次**，
>   所以 §30 的 Buff Breakdown 里那一行 `Damage = …` **必须说明它是"分摊过来"的，不是相加的另一笔**。

---

# 28. Skill Breakdown

每个 Skill 输出：

```text
Cast Count
Total Damage
Damage Share
Average Damage / Cast
Average DPS Contribution
```

例如：

```text
御剑术

Cast Count = 31
Total Damage = 420,000
Damage Share = 35%
Avg Damage = 13,548
DPS Contribution = 7,000
```

---

# 29. Effect Breakdown

每个 Effect 输出：

```text
Execute Count
Damage
Trigger Count
Success Count
Failure Count
```

例如：

```text
E_DoubleAttack

Execute Count = 520
Successful Proc = 103
Proc Rate = 19.8%
Additional Damage = 120,000
```

---

# 30. Buff Breakdown

每个 Buff 输出：

```text
Apply Count
Remove Count
Uptime
Average Duration
Max Stack
Average Stack
Trigger Count
Damage Contribution
```

例如：

```text
剑意

Apply = 15
Uptime = 82.4%
Max Stack = 5
Average Stack = 3.2
Triggers = 128
Damage = 180,000
```

---

# 31. Combat Event Metrics

Simulator 必须记录：

```text
AttackEvent / sec
DamageEvent / sec
EffectEvent / sec
TriggerEvent / sec
```

以及总次数。

用途：

> 分析一个 BD 的输出到底是通过“提高单次伤害”还是“提高事件发生频率”实现。

> ⚠️ **`TriggerEvent / sec` 现在恒为 0**（链式事件未实现，§13）。
> 报表上要么不显示这一行，要么明确标"未启用"——
> 显示成 `0` 会被读成"触发链没有收益"，而真实情况是"触发链还不存在"。
> 前三个（Attack / Damage / Effect）是有效的，而且**正好够回答本节的问题**：
> "提高单次伤害"看 `DamageEvent` 的均值，"提高频率"看 `AttackEvent / sec`。

---

# 32. Skill Cast Metrics

记录：

```text
Skill Cast Count
Skill Cast / sec
Average Cooldown
CD Uptime
```

其中：

```text
CD Uptime
```

可以帮助发现：

> 技能到底是在频繁释放，还是大量时间因为其他原因没有释放。

---

# 33. Buff Uptime

Buff：

```text
Uptime =
Buff Active Time / Simulation Duration
```

例如：

```text
Duration = 60s
Buff Active = 48s

Uptime = 80%
```

---

# 34. Proc Metrics

所有概率机制都需要记录：

```text
Attempt Count
Success Count
Actual Proc Rate
Expected Proc Rate
```

例如：

```text
Double Damage Chance = 20%

Attempts = 1000
Success = 196

Actual Proc Rate = 19.6%
Expected = 20%
```

---

# 35. RNG 模式

系统支持两个模式。

## 35.1 Expected Mode

> 🚨 **改写：Expected Mode 不是本工具要新建的东西 —— 它已经存在，叫 `LevelCurve`。**
>
> 这是原文档**最严重的自相矛盾**：
> §2.1 明令"不重复实现战斗规则"，而 §35 又要求"按期望结果解析求解" ——
> **期望模型本身就是规则的第二个实现路径**，它绕开了逐拍模拟。
>
> 工程对这个矛盾早有处置，而且不是"删掉一个"：**项目刻意保留两条算路，并用自检把它们钉在一起。**
>
> | 算路 | 在哪 | 干什么 |
> | **逐拍模拟** | 逐帧跑 `GameSession` | 权威行为，含时序、快照、目标切换 |
> | **期望模型** | `Core/Data/LevelCurve.cs` | 快、稳、可搜索，**含经济模拟**（每关收入 → 沿修行树贪心买最便宜的一档 → 灵核门封顶） |
>
> 两者**共用同一个 `Core/Data/DamageFormula.cs`**（`AGENTS.md` 的硬约束：伤害公式只许有一份实现）。
> 所以"不重复实现规则"在这里的准确含义是 **"不重复实现公式"**，不是"不许有第二套算法"。
>
> **必须写在文档里的代价（`combat.md` §12.2 的教训）**：
>
> > 错得对称的模型会伪装成正确 —— 模型的净变化 +0.6% 看起来没问题，
> > 其实是两处相反的错误互相抵消。
>
> 所以：**期望模型必须算上模拟里实际生效的每一个乘区**。
> 模型漏掉一个乘区就是静默低估，而且 `--check` **不会**告诉你（它只比"两者是否一致"，
> 若两边都漏同一个乘区，它照样全绿）。
>
> **因此探针的 Expected Mode 口径**：**复用 `LevelCurve.Dps` 的扩展，不写第三份公式。**
> 而且要带一条**对账义务**：同一练度点上，期望值与逐拍模拟值的偏差
> **允许存在，但必须打印出来，并在超阈值时响亮失败**。这是 §63 验收标准的可执行形式。

默认模式的取舍（✅ 与原文一致）：

- 快速、稳定、可重复
- 适合 AI 搜索、适合属性排序

**但它不适合作最终结论** —— 任何要落到数值上的结论，必须由 Simulation Mode 复核。

---

## 35.2 Simulation Mode

真实随机模拟。

支持：

```text
Seed
Iteration Count
```

例如：

```text
Seed = 123456
Iterations = 10000
```

输出：

```text
Mean DPS
Min DPS
Max DPS
P10
P50
P90
Standard Deviation
```

---

# 36. RNG 可重复性

所有 Simulation Mode 必须支持固定 Seed。

相同：

```text
Build
Scenario
Seed
Iteration Count
```

必须得到相同结果。

用途：

> Debug 和自动化测试。

> ✅ **本章就是 Phase 0 的 Probe-Test 01，而且工程可以做得比"结果相同"更强。**
>
> 措辞上有个区别值得点明：
>
> | 说法 | 强度 |
> | "两次跑出来的**最终结果**相同" | 弱 —— 两个 bug 互相抵消也算相同 |
> | "**逐帧快照**逐步相同"（`Effects` + `EnemyState` 序列化） | **强** —— 任何一步的分歧立刻暴露 |
>
> 探针采用后者。理由：本工具要回答"改了这条配置，输出为什么变了"，
> 那么**第一次分歧发生在哪一拍**才是关键信息；只看终值等于把这条线索扔了。
>
> ⚠️ `Iteration Count` 在本工程要谨慎用：一次 60s 的逐拍模拟是 1200 拍 × 全部实体，
> 10000 次迭代不是"多跑一会儿"，是**量级上跑不动**。
> 正确做法是**少迭代 + 报分布**（P10/P50/P90），而不是靠迭代数把方差磨平 ——
> 后者会掩盖真实的波动，而波动本身（比如 20% 触发率的实际表现）正是评估要看的东西（§41）。

---

# 37. 属性边际价值

战斗探针必须支持：

> **Attribute Delta Evaluation**

即：

```text
Base Build
+
一个属性变化
↓
重新模拟
↓
比较结果
```

例如：

```text
Base ATK = 1000
```

测试：

```text
ATK +1%
```

得到：

```text
DPS Before = 10000
DPS After = 10100
```

结果：

```text
DPS Gain = +100
DPS Gain % = +1%
```

> ⚠️ **方法论完全正确（✅ 保留），但要换量纲：差值本身应当记在 SU 上。**
> 上例改写成工程口径是"**SU/秒 从 333 涨到 337**"——
> 因为这个数最终要拿去和"关卡强度需要多少 SU"对比，而那是关卡编辑器的输入。
>
> 另外 §43 那条（价值必须绑定 Scenario）在这里也适用：
> **上面的例子隐含了"Base ATK = 1000"这个练度点**，而 `atk_percent +1%` 的价值
> 在不同练度点上完全不同（低练度时 +1% 攻击很便宜，高练度时可能已经是边际最差的选择）。
> 所以属性价值表**每一行都必须带练度点**，不能只写一个数。

---

# 38. 属性价值不允许硬编码

禁止：

```text
CritRateValue = 0.6
AttackSpeedValue = 1.0
```

属性价值必须通过实际模拟得到。

因为：

> **属性价值依赖当前 Build、技能结构、Buff、Effect 和战斗场景。**

---

# 39. Attribute Delta 类型

支持：

### 百分比属性

例如：

```text
atk_percent +1%
generic_damage +1%
crit_rate +1%
attack_speed +1%
skill_cdr +1%
```

### 绝对属性

例如：

```text
atk_base +10
```

### 离散属性

例如：

```text
projectile_count +1
```

### 特殊能力

例如：

```text
double_damage_chance +10%
```

全部通过：

```text
Before Simulation
↓
Apply Delta
↓
After Simulation
```

> ⚠️ **四类里只有前两类是"属性"（`fightattr.csv` 的字段）；后两类是技能 / Effect 参数。**
> 这个区分很重要，因为它们的 delta 施加方式不同：
>
> | 类别 | 例子 | 实际改的是 |
> | **百分比属性** | `atk_percent` / `crit_rate` / `attack_speed` / `skill_cdr` / `generic_damage` | `fightattr` 字段（**注意 `max_value` 上限**） |
> | **绝对属性** | `atk_base` | 同上 |
> | **离散属性**（原写 `projectile_count`） | 御剑术的**剑支数量** | 这是**技能参数**，不是属性 ⇒ 归 §40 |
> | **特殊能力**（原写 `double_damage_chance`） | "20% 概率双倍" | 这是 **Effect 参数** ⇒ 归 §41 |
>
> **判定方法**：这个量在存档里长什么样？是 `fightattr` 的一格 ⇒ 属性；是某张技能/Effect 表的一列 ⇒ 参数。
> 探针的 `attribute_id` **必须取自 `fightattr.csv` 的实际字段名**，不要自造 `projectile_count` 这种听起来像属性的名字，
> 否则会诱导后面的人往 `fightattr` 里加一个不该在那里存在的字段。

统一评估。

---

# 40. Skill Parameter Value

Skill 参数也使用同样的方法。

例如：

```text
御剑术 Projectile Count +1
```

直接比较：

```text
Base DPS
vs
Projectile +1 DPS
```

输出：

```text
DPS Gain
DPS Gain %
```

---

# 41. Effect Value

特殊 Effect 同样可以评估。

例如：

```text
20% Chance
→ Double Damage
```

测试：

```text
Before
After
```

得到：

```text
DPS Gain = +19.8%
```

而不是人工假设：

```text
20% = +20% DPS
```

---

# 42. Buff Value

例如：

```text
Buff Duration +10%
```

直接模拟：

```text
Base Duration
vs
Duration × 1.1
```

得到实际 DPS 变化。

这样可以自动评估：

```text
Buff Duration
Buff Stack
Buff Trigger Rate
Buff Damage
```

的价值。

---

# 43. 属性价值必须绑定 Scenario

任何结果必须带：

```text
Build
Scenario
Duration
Target Count
Target HP
RNG Mode
```

例如：

```text
+1% Crit Rate
```

不能简单输出：

```text
Value = +0.52%
```

必须输出：

```text
Build = Sword Build A
Scenario = Single Target Infinite HP
Duration = 60s
Value = +0.52%
```

---

# 44. Relative Value

统一输出：

```text
Relative DPS Gain =
(DPS_after - DPS_before) / DPS_before
```

例如：

```text
DPS Before = 10,000
DPS After = 10,200

Relative Gain = +2%
```

---

# 45. Absolute Value

同时输出：

```text
Absolute DPS Gain =
DPS_after - DPS_before
```

例如：

```text
+200 DPS
```

两者都保留。

---

# 46. Equivalent Value

支持将属性换算成同一参考单位。

例如选择：

```text
Reference = ATK%
```

然后：

```text
+10% Crit Rate
≈ +6.4% ATK
```

这里的结果必须由 Simulator 实际搜索/插值获得。

禁止手工指定换算关系。

> ✅ **本章保留，而且它在工程里已经有一个"预订的坑位"。**
>
> `roadmap.md` 的「工程备注：留档的未来项」记着这样一条链
> —— **「角色综合战力统计 → 每关战力需求 → 一键造号」**（2026-10-07 用户提出，明确不在当前轮做）。
> 其中第一步"把修为 / 法术 / 武器 / 参悟 / 剑灵折成一个数"**就是本章的 Equivalent Value**。
>
> 两点衔接说明：
>
> - **参考单位选什么，会决定这个战力数好不好用。** 选 `ATK%` 的好处是直观，
>   坏处是它**不含生存与功能**（战力要涵盖气血、闪避、控制，不只是输出）。
>   这一步是真实设计决策，**不在探针里定**，属于 Phase 3 的范畴。
> - **"一键造号"不能绕过灵核账本**：存档校验有"灵核余额 ≤ 首杀数 + 调试发放量"这条硬约束，
>   灌存档必须走"调试发放"那条既有通道记账（见 `GameSession` 的 GM 发放）。
>
> 关卡编辑器（`UI/LevelEditor.cs`）里的**「试打这一关」是这条链的第一步**——
> 它已经能把人送到指定关卡，缺的只是"把角色的练度也一起摆到我们要的位置"。

---

# 47. 属性价值报告

标准报告：

```text
Attribute Value

Attribute          Delta       DPS Gain      Relative Gain

ATK%               +1%         +102          +1.02%
Attack Speed       +1%         +98           +0.98%
Generic Damage     +1%         +100          +1.00%
Crit Rate          +1%         +51           +0.51%
Crit Damage         +1%         +18           +0.18%
Skill CDR           +1%         +73           +0.73%
```

> ⚠️ **这张表缺两个必需的表头字段，缺了就是废表：**
>
> | 缺什么 | 为什么必须有 |
> | **练度点**（Build 基线） | `+1% Crit Rate` 在"暴击率 5%"和"暴击率 80%"下价值差好几倍。没有基线，这个数换一批配置就作废（§38 自己说了） |
> | **Scenario** | 单体场景里 Attack Speed 值钱，群怪场景里可能被 AoE 碾压（§43） |
>
> 另外两个工程侧的提醒：
>
> - **`Skill CDR +1%` 这一行在有增益类法术的 build 里会偏高** ——
>   因为增益类法术**不吃 CDR**（§12），那部分收益不存在。表里要能看出"这行是按哪些法术算的"。
> - **`Crit Rate +1%` 受 `crit_rate` 上限 1 约束**（`fightattr.max_value`）：
>   基线接近满暴时，这一行的收益必须趋近 0，而不是保持线性 ——
>   如果表里还是 +0.51%，说明**探针没读上限**，那张表整体不可信。

---

# 48. Build Comparison

支持多个 Build 同场景比较。

例如：

```text
Build A
Build B
Build C
```

输出：

```text
SU / 秒        ← 纲
TTK            ← 纲（须带靶子 SU）
60s DPS
1s DPS
5s DPS
10s DPS
30s DPS
120s DPS
溢出           ← 原写 "Overkill"，统一到 DamageStats 口径
```

并自动排序。

> ✅ **"比较 Build" 这件事完全成立** —— 但注意它的准确含义是
> **"同一个角色换配置"**，不是"同一个配置换循环"（工程里没有循环优先级，见 §10）。
> 报表上要写清楚，否则用户会以为"循环"是可调参数。
>
> ⚠️ **排序键必须是 SU/TTK，不是 60s DPS**（否则又回到 §20 的第二把尺子）。
> 而且**不同 Scenario 下排序会变**（单体冠军在群怪场景可能垫底）——
> 所以**排名必须带 Scenario 标签**，一个没有场景的"Build 排行榜"是错的。

---

# 49. Build 不只比较 DPS

最终 Build Report 应至少包括：

```text
SU / 秒              ← 纲
TTK                  ← 纲
Sustained DPS        ← 场景指标
Burst DPS            ← 场景指标
有效 DPS
溢出
Damage Distribution
Skill Usage
Buff Uptime
Proc Rate
```

避免：

> 单纯按照 60s DPS 排名。

> ✅ **本章的立意完全正确，而且正是工程最需要的**：一个只看 60s DPS 的排行榜
> 会把"斩杀流""爆发流""持续流"压成一个数，而这三者在本工程里是三种不同的价值投放方式。
>
> ⚠️ 但**光加指标还不够** —— 指标多了还是会被压成一个排名。
> 正确做法是**按 Build 的"形状"分组**（爆发 / 持续 / 斩杀 / 群怪），
> 组内比 SU/TTK，组间不做单一排名。这直接对上了 `docs/design/value_inventory.md`
> 那套"战斗价值分类"的意图 —— 探针的输出应当**用游戏已经认的价值分层来组织**，
> 而不是自己发明一套分类。

---

# 50. 标准测试场景

> ⚠️ **改写：靶子血量不能随手写 `1,000,000`。**
> 工程的一切强度都挂在 **SU 台阶**上（标准怪 = 1 SU = 30 HP，精英 = 10 SU，BOSS = 75 SU，
> 台阶随关卡按 `LevelCurve` 的七个倍率列放大）。
> 探针的标准场景**默认取 `LevelCurve` 的真实血量**，
> 自由血量要显式标成"自定义"，不要混进标准场景 ——
> 否则跑出来的 TTK 无法与关卡编辑器里的强度需求对齐，这把工具就白做了。
>
> 每个场景还**必须带上层构成 / 间距 / 是否聚怪**（§19），并随结果一起打印。

V1 内置以下 Scenario（`HP` 按 SU 记，探针内部换算成真实血量）：

## Scenario 01：Single Target Infinite

```text
Targets     = 1
HP          = Infinite（1 SU 量级的标准怪形态）
Layer       = 地面
Spread      = ——（单目标无意义）
Duration    = 60s
```

用途：

> 纯输出频率与基础乘区。

> 🚨 **本场景下「斩杀 / 利用状态 / 易伤」全部失效**（§17），报表标题必须标出来。

---

## Scenario 02：Single Target Boss

```text
Targets     = 1
HP          = 75 SU（BOSS 标准值）
Layer       = 地面
Duration    = 可按 TTK 自动延长
```

用途：

> **TTK**（主指标）+ 长线输出。

---

## Scenario 03：3 Target

```text
Targets     = 3
HP          = Infinite
Layer       = 地面
Spread      = 中（能同时覆盖 3 只的距离）
Gathered    = 是
Duration    = 60s
```

用途：

> 小规模 AoE。

> ⚠️ **健全性检查：SU 不得恰好等于单目标的 3 倍**（§19）。

---

## Scenario 04：5 Target

```text
Targets     = 5
HP          = Infinite
Layer       = 地面
Spread      = 中
Gathered    = 是
Duration    = 60s
```

用途：

> 群怪输出。

> ⚠️ **建议再加一条 Scenario 05：混合层靶场**（地面 + 空中）——
> 只打地面的招式在纯空中靶场的 SU **必须为 0**。这是层规则唯一能真正测出问题的地方。

---

# 51. 标准 Scenario 不包含外部因素

V1 不加入：

```text
玩家操作失误
移动
躲避
网络
动画打断
怪物 AI
随机地图
```

除非这些因素已经被正式战斗逻辑定义为可计算事件。

> ✅ **这一条与工程的 `SandboxMode` 完全一致，而且工程已经把它做成了开关。**
>
> `SandboxMode` 关掉的正好是这张表里的**移动**与**怪物 AI**（连同刷怪、通关、落盘）。
> 所以探针用沙盒不是"退而求其次"，而是**本节的直接实现**。
>
> ⚠️ 但最后那句"除非已被正式战斗逻辑定义为可计算事件"要**当心它的方向**：
> 移动、怪物 AI 在正式战斗里**恰恰是**可计算事件（怪物真的会走过来、真的会进入射程）——
> 它们被排除，是因为**场景刻意不摆**（靶子定身放远），不是因为"游戏里没有"。
>
> | 排除的原因 | 例子 | 后果 |
> | **目标本就不含此机制** | 玩家操作失误、网络、随机地图 | 无害，这是纯战斗底盘评估 |
> | **机制存在但场景关掉了** | 移动、怪物 AI、刷怪 | **结论的有效范围受限** —— 报表必须写明"本结论假设靶子不移动" |
>
> 第二类的典型后果：**射程不够的 build 在定身靶场里看不出问题**，
> 因为靶子不会走出射程（也不会走进来）。所以任何关于"射程 / 机动"的结论，
> **必须换到真题关卡跑**（关卡编辑器的「试打这一关」正是为此存在）。

---

# 52. 评估报告分层

建议 AI 输出三个层级。

## Layer 1：Core

```text
SU / 秒        ← 纲
TTK            ← 纲（带靶子 SU）
DPS
Burst DPS
有效 DPS
```

## Layer 2：Combat

```text
Damage Breakdown
Skill Usage
Effect Usage
Buff Uptime
Proc
溢出            ← 原写 "Overkill"
```

## Layer 3：Debug

```text
Event Count
Event Timeline
DamageEvent
Trigger Chain     ← 未实现，标"未启用"
RNG Seed
```

普通数值查看不需要展示 Debug Layer。

> ⚠️ **Layer 3 的 `Trigger Chain` 在 V1 是空的**（链式事件未实现，§13）。
> **不要为了"这层有内容"去实现它** —— 那是拿工具的进度倒逼玩法设计。
> 标"未启用"比删掉好：它提醒读者"触发链的收益还没被计入任何 SU"，
> 这个信息很重要（缺了它，将来引入触发链时所有历史 SU 都要重算）。

---

# 53. Event Log

Simulator 必须支持可选 Event Log。

> 🚨 **改写：原文的示例把暴击写成了 `Crit = true` 与 `DMG3 = ×1.8` 并列 —— 这会教出错误的心智模型。**
> 暴击**判定**在事件链里（③ 一带），**倍率**在 DMG3（⑤）。两者不是同一件事，
> 而且判定结果在**出手时就快照**（§3.4），之后的所有派生结算共用它。
> 写成并列，等于暗示"暴击是一次乘法"，于是没人会去实现快照。

按 §13 的九步链重画：

```text
0.000  AttackEvent     御剑术（出手 → 快照 DMG1=2000 / 暴击判定=真 / 倍率=1.8 / 伤害窗口）
0.100  TargetResolution 选中 m_03（地面层，距离 68）
0.100  HitResolution    命中（闪避未触发）
0.100  DamageEvent      名义 3600
0.100  DMG3             Generic 1.0 × Critical 1.8 × Build 1.0 × Vulnerability 1.0
0.100  ApplyDamage      有效 3600 / 溢出 0 / 命中 +1
0.100  OnCrit           醉仙望月步冷却 −2.0s（受 §12 下限约束）
1.000  BuffTrigger      B001（白名单 on_skill_cast / on_crit）
```

Event Log 用于：

- Debug
- 验证技能逻辑
- 验证数值
- 查找异常 Trigger
- 查找无限循环
- **查找"快照被违反"** —— 同一次出手的多段事件，若 `Critical` 值在各段间变化，即为 bug

> ⚠️ `查找无限循环` 在本工程**暂无对象**（链式事件未实现，§13）。
> 保留这条用途，但 V1 写不出针对它的用例。

---

# 54. 防止 Trigger 无限循环

Simulator 必须具有事件安全机制。

每个 EventChain 携带：

```text
chain_id
depth
source
```

并设置：

```text
max_chain_depth
```

✅ 结构与 `combat.md` §8 第 2/4 条一致（同一事件不得触发自身）。

> 🚨 **加一个取值：`max_chain_depth = 8`**（原文写的 20 作废）。
> 这条上限在 `combat.md` §8 第 3 条**已经拍板**，探针不许另取一个值 ——
> 两处各写一个深度上限，就是又一把小尺子。
>
> 另：这一整节**目前是空条款** —— 链式事件（第 ⑨ 步）尚未实现，
> 没有任何 Trigger 能产生新的 AttackEvent，所以深度永远到不了 1。
> 保留为纪律，别为它写"验证确实会停下"的测试（那测的是不存在的东西）。

---

# 55. Simulator 与正式战斗系统的边界

> ⚠️ **改写。原文的 "Simulator 负责管理 EventQueue" 直接作废 —— 本工程不建队列。**

探针（Probe）负责：

```text
推进时间（固定步长，与正式战斗同一个 fixed_step）
创建模拟场景（靶子摆位、层、间距）
按场景需要补充 / 重摆靶子
记录统计
运行 Scenario
运行多次 RNG（同 seed 确定性）
输出结果
```

正式战斗系统负责（**探针一律不得重写**）：

```text
Skill 释放规则
Effect 执行
Buff 生命周期与时间线
AttackEvent（含出手快照）
TargetResolution / HitResolution
DMG1 / DMG3
DamageStats 记账
```

原则（✅ 原文这句是对的，保留）：

> **Simulator 驱动战斗，CombatSystem 决定战斗规则。**

**具体的落地形态就是 `GameSession` + `SandboxMode`**（`Features/Battle/GameSession.cs:156`）：

- 跳过：移动 / 激活格子 / 刷怪 / 玩家死亡 / 通关 / 落盘
- 照常：冷却流逝、施法、效果推进、命中结算、状态计时、**敌人死亡与移除**（`GameSession.cs:247`）
- 已有先例：`UI/SkillPreview.cs:120`（GM 技能预览走的正是这条路）

**Phase 0.5 已落地的形态**（2026-10-08）：

```text
CombatProbe（Features/Battle/CombatProbe.cs，纯 C#，不依赖 Godot）
    PlanWaveField(order)      纯计算：这一关一波真实刷怪摆在哪、摆几只、什么层
    Measure(ProbeRequest)     跑一场：预热 W 秒 → 清账 → 记账 N 秒
    Reconcile(order, save)    两个练度点各跑一场，折成"对模型倍率"
```

本节开头那份"探针负责 / 正式战斗负责"的分工**逐条成立**：
探针管推进时间、摆靶、补靶、读数；出手规则、伤害公式、记账全在 `GameSession` 与 `DamageStats` 里，探针一个字没重写。

其中三条是这一轮踩出来的，**不是设计选择而是必然**：

- `Effective + Overkill` 才是标称伤害。只记 `Effective` 会把每一次过量击杀悄悄扣掉 —— 而模型侧的 `Dps` 是"输出上限"。
- 出手计数**不能**抄 `UI/Audio.cs` 那条"冷却 0 → 正"的边沿。冷却衰减写的是 `Math.Max(0, cd − dt×factor)`，
  而 `CastSkills` 在**同一个 `Step`** 的后半段就把冷却写回正值 —— 从 `Step` 之后观察，那个 0 永远看不到。
  改用"冷却**涨上去**"（冷却只减或跳增，等价）。也**不能**比 `Effects` 集合差：增益类法术走 `CastBuff`，根本不产生 `CombatEffect`。
- 靶子**不摆清一色地面满血木桩**：真实波次混入 `layer = air`（地面攻击经 `LayerHit` 打不到），
  横向铺开到 `40 + i×100` 会掉出射程，而且 `SwordUpgrade` / 斩杀 / 利用状态都靠**目标血量比例**——
  给无限血会让它们静默失效。

---

# 56. 推荐代码结构

> 🚨 **改写：`DpsSimulator/` 这个目录作废。**
> `AGENTS.md` 的分工是：**纯逻辑在 `Core/` + `Features/`，工具在 `tools/`**；
> 而且明确"伤害公式只许有一份实现"。
> 建一个 `DpsSimulator/` 平行引擎，正是 §2.1 要防的事，而且名字里带 DPS 会把第二把尺子带回来。

> 🚨 **再改一次（2026-10-08）：`tools/CombatProbe/` 这个目录也不再建。**
> 探针的**出口并进了关卡编辑器**——编辑器**已经在**调 `LevelCurve.TryCompute` 画建议虚线与偏差，
> 「试打这一关」按钮**也已经存在**，探针是同一件事的下一步。单开界面等于把
> 2026-10-07 拍板的「编辑器为准」分工倒回去。排期与理由记在 [roadmap.md](../planning/roadmap.md) 的 Phase 0.5。

实际结构：

```text
idle-sword/Features/Battle/CombatProbe.cs     内核（纯 C#；与 LevelCurve 共用同一份模型）
    TargetPlacement / ProbePoint / ProbeRequest / ProbeResult / ProbeReconciliation
    PlanWaveField(order)   这一关一波真刷的靶位清单（纯计算，自检可直接断言）
    Measure(request)       一场：预热 → 清账 → 记账
    Reconcile(order, save) 模型期望点 + 当前存档点，两边折成对模型倍率

idle-sword/UI/LevelEditor.cs                  「战斗探针」只读节（屏上的出口）
idle-sword/Core/Data/LevelCurve.cs            期望模型（探针只是调用方，不写第三份公式）
```

**Scenario 现在只有一种**：关卡自己的真实波次（`PlanWaveField`）。"目标数 / 层 / 间距 / 聚怪 / 时长 / seed"
这些旋钮里只有 `Warmup / Window / Dt / Seed` 是显式的 `ProbeRequest` 字段；靶子那几项**由关卡表决定，不另开旋钮**——
手调出来的场景对不上真实关卡，正是 Phase 0 那 1.638 偏差的来源。

**期望模式不在这里** —— 它复用 `Core/Data/LevelCurve.cs`，探针只是调用方。

---

# 57. SimulationConfig

> ⚠️ **`skill_priority` 这一项删除**（工程里没有这个配置位，见 §10）。

核心配置：

```text
duration
target_count
target_hp             （按 SU 记，或显式标 Infinity）
target_layer          （层构成：地面 / 空中 / 混合）
target_spread         （间距）
gathered              （是否聚怪）
rng_mode              （Expected / Simulation）
random_seed
```

例如：

```text
duration    = 60
target_count = 1
target_hp    = Infinity
target_layer = 地面
rng_mode     = Expected
seed         = 123456
```

**每一项都必须随结果打印**（§19 / §43）—— 场景参数不进报表，两次跑出不同数字时没人知道原因。

---

# 58. SimulationResult

至少包含（**字段名对齐 `DamageStats` 的既有口径，不另起一套**）：

```text
total_名义伤害
total_有效伤害
total_溢出
total_命中次数

su_per_second          ← 主指标
ttk                    ← 主指标
ttk_target_su          ← TTK 必须附带"靶子值多少 SU"（§24）

dps_1s / 3s / 5s / 10s / 30s / 60s / 120s   ← 场景指标，须能与 SU 互推（§20）

skill_statistics
effect_statistics
buff_statistics
event_statistics
rng_statistics
```

---

# 59. AttributeEvaluationResult

至少包含：

```text
attribute_id

base_value
delta_value

su_before          ← 纲
su_after           ← 纲
ttk_before
ttk_after

dps_before         ← 场景指标
dps_after

absolute_gain
relative_gain

build_point        ← 练度点（缺了这张表就不可复用，见 §47）
scenario           ← 场景标签（§43 强制）
capped             ← 是否被 fightattr.max_value 夹住（暴击率 / 攻速 / CDR 都会）
```

> ⚠️ **`capped` 这个字段必须有。** 属性价值表最容易骗人的情形是：
> 基线已经贴着上限，探针却还在给一个正的 `+1%` 收益 ——
> 用户看到"暴击率 +1% 值 X"就继续堆暴击，实际一点用没有。
> 标出 `capped = 是`，等于在报表上写一句"这条已经开始被上限吃掉了"。见 §47。

---

# 60. V1 实现优先级

> 🚨 **改写：原文的 "Phase 1：SimulationContext / EventQueue / SimulationRunner" 整段作废**
> （`EventQueue` 不建，见 §6）。而且原文把"接入正式战斗系统"放在 Phase 2 ——
> **顺序反了**：复用 `GameSession` 是**第一步**，不是第二步，因为它是"不重复实现"这条原则的落地方式。
>
> 实际排期**按内容定形的程度切**，不按功能切（见 §61 后的「落地顺序」）：

### Phase 0（现在就能做，不依赖任何内容定形）

```text
战斗结算的可复现性对账 —— 即 §63 的验收标准，工程已欠着它

① 同 seed 逐帧确定性   ：同一 Build + Scenario + seed 两跑，Effects 与 EnemyState 快照逐步相同
② 期望模型 vs 逐拍模拟 ：比较 LevelCurve.Dps 与逐拍 DPS，偏差打印 + 超阈值响亮失败
```

落点在 `tests/`（与现有 116 条断言同一纪律）。

> 原计划里"用改造前的整树快照跑对拍"**已不可行** ——
> `%LOCALAPPDATA%\Temp\idle-skill-refactor\baseline\` 已被清理。
> 所以 Phase 0 改成**可长期复用的机制**，而不是一次性对比。

### Phase 1（数值批次之前必须先行，是设计决定不是机械修复）

```text
定「期望参悟练度」曲线

SwordUpgrade.csv 给每个输出法术配了 4 行 damage_percent × 20 级 × 8% ⇒ 满配 +640%（SkillRate ×7.4），
而期望模型里一项都没算。这是 --write 的唯一阻塞项，也是 Expected Mode 的前置。
```

### Phase 2（数值批次时，与它一起落地）

```text
编辑器「试打这一关」那一侧的量化节（内核继续在 Features/Battle/CombatProbe.cs）

Scenario: 现在只有"关卡自己的真实波次"一种（PlanWaveField）；
          要再加 Scenario 类型，得有真实关卡逼出来的理由（见 §56 那条"不另开旋钮"）
Report:   SU / TTK 为主，DPS / 拆解 / Buff 覆盖率 / Proc 率为辅，全部带 Scenario 标签
Expected Mode 复用 LevelCurve.Dps（不写第三份公式）
```

**Phase 0.5 已完成的部分**（2026-10-08，在 Phase 2 之前就落地了）：
固定步长推进（与正式战斗同一个 50ms `fixed_step`）、`GameSession` + `SandboxMode` 驱动、
`Measure` / `Reconcile`、逐式「实测命中 / 次」对照模型的期望命中数、以及屏上那个只读节。
它读的是**当次的关卡表与当次的存档**，所以不是一次性对拍脚本，而是可长期复用的机制。

### Phase 3（内容定形后）

```text
Attribute Delta Evaluation
Skill Parameter Evaluation
Effect Evaluation
Buff Evaluation
Equivalent Value 换算
Build Comparison

前提：修行树定形 + 参悟期望练度曲线已定（Phase 1）+ 死配置清零
```

---

# 61. V1 明确不做

以下内容暂不进入第一版：

```text
动画模拟
角色移动
真实投射物轨迹
碰撞箱
复杂怪物 AI
操作模拟
装备掉落
经济系统
地图生成
完整刷图模拟
```

也不做：

```text
“DPS Score = 0~100”
```

第一版只提供真实、可解释的原始指标。

> 🚨 **但「经济系统」这一条必须单独拎出来 —— 它与本工具的核心用途冲突。**
>
> 探针的三大用途之一是**关卡强度调整**，而工程的关卡强度**不是靠 DPS 推出来的，是靠经济模拟推出来的**：
> `LevelCurve` 的模型做的是"这一关的收入 → 沿修行树贪心买最便宜的一档 → 被灵核门封顶"，
> 于是得出"打到第 N 关时，玩家的期望练度是多少"。
>
> 也就是说，回答"这一关强度够不够"需要**两个输入**：
>
> | 输入 | 来源 |
> | 玩家在这一关**买得起多少练度** | **经济模拟**（`LevelCurve`） |
> | 这份练度**打不打得过** | **逐拍模拟**（探针） |
>
> **两者必须一起算。** 单靠 DPS 只能回答后者的一半 ——
> 而且会给出一个很危险的结果："这份练度能打过"（对）但"玩家根本到不了这份练度"（错）。
>
> **处置**：探针本身不重写经济系统（复用 `LevelCurve`），
> 但 §60 的 Phase 2 验收里必须包含"逐级跑一遍，看每关的期望练度能否打过该关"这条端到端检查。

---

# 62. 自动化测试要求

> 🚨 **改写：原文的 Test 01–10 大部分测的是"公式"，而工程的 `tests/` 已经有 116 条断言在测它们了**
> （DMG1 / SMG 乘区 / 暴击 / Buff 时间线 / 多段……）。
> 探针再写一遍的后果不是"多一层保险"，而是**同一件事两处断言**：
> 改口径时要改两处，早晚漏一处，然后两处说法不一致。
>
> 更要命的是**其中有两条测的是不存在的机制**：
>
> | 原 Test | 问题 |
> | --- | --- |
> | **Test 07 Extra Attack** | 验的是 `Trigger → AttackEvent`。链式事件**未实现**（§13）⇒ **没有任何输入能让它变红 ⇒ 空断言** |
> | **Test 09 OnKill** | 同上，`OnKill` 未实现 ⇒ 空断言 |
>
> 空断言比没有断言更糟：它让"覆盖了触发链"这件事**看起来成立**。
> 工程纪律是"会自己静默消失的断言等于没有" —— 空断言是它的极端形式。
>
> 正确的做法是：**探针只写"引擎级"的断言**，即那些只有"真的跑起来"才能验证、
> 公式级断言覆盖不到的东西。

### 探针必须有的三条（也就是 Phase 0 的落点）

**Probe-Test 01：同 seed 逐帧确定性**

```text
同一 Build + Scenario + seed，跑两遍
→ Effects 与 EnemyState 的序列化快照逐步相同
```

这是整个 harness 的地基。它还顺带覆盖"技能三层结构改造有没有改语义"那类问题 ——
**比一次性对拍更耐用**（快照已经没了，这个机制还在）。

> ⚠️ 断言必须**实测过"破坏它就会红"**：故意在 `Launch` 里多摇一次随机数，它必须失败。

**Probe-Test 02：期望模型 vs 逐拍模拟对账**

```text
同一练度点
→ LevelCurve 的期望 DPS  vs  逐拍跑出来的 DPS
→ 偏差打印；超阈值时响亮失败
```

这是 `combat.md` §12.2 那条教训的可执行形式：**"错得对称的模型会伪装成正确"**。
允许有系统性偏差（模型本来就抽象），但**不许是静默的**。

> ⚠️ 同样要实测：故意把模型漏掉一扇伤害窗，它必须失败。

**Probe-Test 03：场景切换不改规则**

```text
换 Scenario（单目标 / 多目标 / 混合层 / 有限血 / 无限血）
→ 战斗规则本身不变，只有摆位与靶子变
```

这条防的是"为了让某个场景跑通而在探针里加特判" —— 那正是 §2.1 要防的第二份实现的开端。

### ✅ 明确不做

- **不重复写公式级断言**（DMG1、乘区、暴击、Buff 时间线……）—— 已有 116 条。
- **不为未实现机制写断言**（Extra Attack / OnKill / Trigger / 链式深度）—— 会是空断言。
- **不在 Phase 3 之前写定量断言** —— 属性价值表要等修行树定形，现在钉死一个数，下周修树就全红，
  然后就会被"顺手放宽阈值"，从此形同虚设。

---

# 63. 最重要的验收标准

战斗探针 V1 是否合格，不以“能算出 DPS”为标准。

必须满足：

### 规则一致

Simulator 与正式战斗使用同一套：

```text
Skill
Effect
Buff
Combat
Damage
```

### 结果可重复

固定：

```text
Build
Scenario
Seed
```

可以得到稳定结果。

### 结果可解释

任何 DPS 变化都可以追溯到：

```text
Skill
Effect
Buff
DamageEvent
Attribute
```

### 结果可拆解

能够回答：

> “为什么这个 Build DPS 更高？”

而不是只输出：

```text
Build A = 12,480
Build B = 11,930
```

---

> ✅ **本章是整份文档最有价值的一条，原样保留 —— 而且它现在有落点了。**
>
> 「规则一致」+「结果可重复」= **§60 的 Phase 0**，落点在 `tests/`（见 §62 的 Probe-Test 01/02）。
> 这两条**不依赖任何内容定形**，所以现在就能做，工程也本来就欠着它。
>
> 补一句口径：本章说"不以能算出 DPS 为标准" —— 在本工程更准确的说法是
> **"不以能算出 DPS 为标准，以能算出并解释 SU / TTK 为标准"**（§20）。
> 一个只会吐 DPS 数字的工具，**恰好就是本工程最需要避免的那把第二把尺子**。

---

# 64. 最终系统定位

> 🚨 **改写：原图有四个错误。**
>
> 1. **`Event Queue` 不建**（§6）—— 换成 `GameSession` 固定步长推进。
> 2. **`DMG2` 被画成链条上的一级**（在 AttackEvent 与 DamageEvent 之间）——
>    DMG2 不是一级，它是**整条链的顺序本身**（§13）。
> 3. **`DMG3` 被画在 `DamageEvent` 之后** —— 顺序反了：**DMG3 在 ApplyDamage 之前**（§13 的 ⑤）。
> 4. **`DPS Evaluator` 居顶** —— 顶层应该是 **SU / TTK**，DPS 是它的下游换算。

重画：

```text
                   Probe Report
             （SU / TTK 为纲，带 Scenario 标签）
                          │
                    CombatProbe 内核
          （Features/Battle/，屏上是编辑器那一节）
                          │
        ┌─────────────────┴─────────────────┐
        ↓                                   ↓
  GameSession + SandboxMode          Core/Data/LevelCurve
  （逐拍模拟，权威行为）              （期望模型 + 经济模拟）
  Features/Battle/CombatProbe.cs  ←→ 探针是这一层的调用方（纯 C#）
                                     出口：关卡编辑器「战斗探针」节
        │                                   │
        └─────────────┬─────────────────────┘
                      ↓
        共用 Core/Data/DamageFormula.cs
        （AGENTS.md：伤害公式只许有一份实现）
                      │
        ┌─────────────┴─────────────┐
        ↓                           ↓
  Skill / Effect / Buff       TargetResolution
  → AttackEvent（出手快照）     / HitResolution
        │
        └──── 九步事件链（§13）：
              ① Attack  ② Target  ③ Hit  ④ Damage
              ⑤ DMG3    ⑥ Apply   ⑦ OnHit/OnCrit
              ⑧ Death→OnKill       ⑨ Chain（未实现）
                      │
                 DamageStats 记账
              （名义 / 有效 / 溢出 / 命中）
                      │
        ┌─────────────┼─────────────┐
        ↓             ↓             ↓
    SU / TTK    Damage Breakdown   Buff / Proc
   （纲）        Skill / Effect     覆盖率 / 触发率
        │
   ┌────┴────┬──────────┐
   ↓         ↓          ↓
 DPS      Attribute    Build
（场景指标）Value      Comparison
```

最终目标不是做一个“DPS 计算器”。

而是建立一套：

> **可重复、可解释、可自动比较的战斗数值实验环境。**

以后任何一个数值设计，都可以变成一个实验：

```text
当前 Build
    ↓
修改一个变量
    ↓
运行探针
    ↓
比较 Before / After
    ↓
得到 SU / TTK / 事件 / 属性价值变化
```

因此：

> **属性、技能、Effect、Buff、BD 不再需要靠设计师凭感觉判断强弱，而可以通过同一套战斗规则进行量化验证。**
>
> ✅ 这一句是整份文档的立意，**完全成立，也是本工程的真实需求**。
> 唯一要加的前提是：**"同一套战斗规则"意味着复用 `GameSession`，不是新写一套。**
> 这条前提一旦丢，工具越强，与游戏实际行为的偏离越大 —— 而那种偏离是最难发现的，
> 因为它看起来精确。

---

# 附：与《战斗底层规则》的差异

本文原稿脱离工程撰写，与 [combat.md](../design/combat.md) 的既有口径有 9 处冲突（4 处架构级）。
下面**逐条留档**，供后来者查"为什么这里和原始方案长得不一样"。
（手法与 combat.md §11、fightattr.md §6 一致：外部规范并入时逐条对账，差异单列。）

## 🚨 架构级（4 条，已改）

| 位置 | 原稿 | 现口径 | 依据 |
| **§4 / §6 / §55 / §56** | 离散事件模拟（DES），`EventQueue` 驱动、按 `event_time` 排序 | **固定步长逐拍模拟**，直接跑 `GameSession` + `SandboxMode`（`fixed_step`, 50ms） | `EventQueue` 本身就是战斗推进规则的第二份实现，违反本文 §2.1 自己 |
| **§35.1** | Expected Mode 是"新建的一种模式" | Expected Mode **已存在**，是 `Core/Data/LevelCurve.cs` 的扩展；两条算路共用 `DamageFormula.cs`，由自检钉一致性 | 与 §2.1 字面冲突，须显式承认"期望模型是刻意的第二算路" |
| **§14 / §53 / §64** | DMG2 完全没出现；§64 把它画成一个乘区 | **DMG2 是九步事件顺序**，不是倍率。**暴击判定在链里、倍率在 DMG3** | `combat.md` §3 / §3.2 |
| **§3** | "不模拟投射物飞行时间" | **飞行时间是战斗事件**（`CombatEffect.Timer` + `Trajectory` + `speed`），且决定谁挨打；可删的只是贴图 | 排除它 ⇒ 假设瞬时到达 ⇒ 移动靶结论偏乐观 |

## ⚠️ 玩法模型不符（4 条，已改）

| 位置 | 现状 |
| **§9 / §10 / §11** | 工程**没有技能优先级**（法术冷却好就放）；**普攻与法术并行**，不是兜底；`trigger_chance > 0` 的神通只在普攻出手时按概率触发；`skill_priority` **没有配置位** |
| **§12** | 补两条：**增益类法术既不吃攻速也不吃 CDR**；**暴击缩冷却按秒扣且服从 `该法术冷却 ÷ (1 + skill_cdr 上限)` 下限** |
| **§17** | 补警告：无限血 ⇒ **斩杀 / 利用状态 / 易伤 三项全部失效**（三条最有价值的 Build 倍率在这里量成 0） |
| **§19** | 场景参数不只 `target_count`：还要**层构成 / 间距 / 是否聚怪**；`highest_hp` 与 `aoe_all` 是两条独立路径 |

## 📐 口径级（5 条，已改）

| 位置 | 现状 |
| **§20 / §23** | **SU / TTK 为纲**；60s DPS 是场景指标，且**必须能与 SU 互推**（桥是 `combat.md` §5 的期望 DPS 公式） |
| **§26** | **整章删除** —— 与 §25 自相矛盾，且"对已死目标继续打"结构上不可能（`GameSession.cs:247` 同一步内移除死亡敌人） |
| **§54** | `max_chain_depth` **20 → 8**（`combat.md` §8 第 3 条已拍板） |
| **§7 / §13 / §16 / §31 / §52** | 工程**没有事件总线**；已落地的只有 `SkillBuffTrigger.csv`（白名单仅 `on_crit` / `on_skill_cast`）。**不许把 `on_hit` / `on_kill` 提前填进白名单** —— 那是"永不执行的分支"。`Trigger` 类指标一律标"未启用" |
| **§11** | 补：间隔**夹在 `fightattr.max_value`**（`attack_speed` 上限 3、`skill_cdr` 上限 1），夹的是**合计值** |

## 🔧 落地级（4 条，已改）

| 位置 | 现状 |
| **§56** | `DpsSimulator/` 目录作废；**再改一次**：`tools/CombatProbe/` 也不建 —— 内核在 `Features/Battle/CombatProbe.cs`，出口是关卡编辑器那一节（理由见 §56 与 roadmap 的 Phase 0.5） |
| **§62** | Test 01–10 与既有 116 条断言大量重叠；**Test 07（Extra Attack）/ Test 09（OnKill）验的是未实现机制 ⇒ 空断言**。改为三条引擎级断言（同 seed 确定性 / 期望 vs 模拟对账 / 换场景不改规则） |
| **§57 / §58 / §59** | 字段与 `DamageStats` 既有口径**对齐**（名义 / 有效 / 溢出 / 命中），不并存；补 `su_*` / `ttk_*` / `build_point` / `capped` |
| **§61** | "不做经济系统"与用途冲突 —— 关卡强度是**经济模拟**推出来的，必须与逐拍模拟**一起算**（探针复用 `LevelCurve`，不重写） |

## ✅ 原样保留（9 条以外，这些是对的）

| 位置 | 为什么留 |
| **§2.1 + §63** | 不重复实现规则 / "同结果"验收 —— 整份文档最有价值的一条 |
| **§37 / §38** | 属性边际价值必须**实测**、**禁止硬编码** `CritRateValue = 0.6` |
| **§41** | "20% 概率双倍"要实测出 **+19.8%** 而非假设 +20% |
| **§43** | 属性价值必须**绑定 Scenario** |
| **§46** | Equivalent Value —— 正是 `roadmap.md` 留档的「角色综合战力统计」的第一步 |
| **§54** | 事件安全（`chain_id` / `depth` / 同一事件不得触发自身）—— 与 `combat.md` §8 第 2/4 条一致 |
| **§25** | 名义 / 有效 / 溢出 / 命中的定义 —— 与 `DamageStats` 契约一致 |
| **§62（立意）** | "必须有固定测试案例" —— 只是**案例选取**改了，纪律保留 |

## 未在本文展开、但工程已欠着的两笔账

1. **期望模型漏掉参悟**：`SwordUpgrade.csv` 给每个输出法术配了 `damage_percent` × 20 级 × 8%
   ⇒ 满配 **+640%**（`SkillRate ×7.4`），而 `LevelCurve` 里**一项都没算**。
   这是"期望模型必须算上模拟里实际生效的每一个乘区"那条约束的**现存违例**。
   见 `combat.md` §12.4 第 2 条与 `balance_ttk.md` 顶部。**属设计决定，不是机械修复。**
   探针的屏上表现是：**存档点的比值系统性高于模型点**（模型点不给参悟，存档点有）。
2. **`level.csv` 与模型的 7 倍偏差**（100 行里 394 格不符），以及模型里
   第 1 关仍是 `WaveSize = 3`（而 `level_001` 引用的是 `count_max = 1` 的单只教学波）。
   这两笔与本文的 Phase 1 同批处理。第 2 笔在探针屏上**如实显示但不判红**（`ProbeReconciliation.Judge`）。

---

# 附二：Phase 0.5 落地的实测结论（2026-10-08）

这一节记的是**探针第一次给出的、计划里猜不到的数**，`--check` 不会重复印它们。

## 1. 起手齐射的假设被证伪

改前的对账夹具把比值推到 **60 关 1.638**，当时列的嫌疑名单第一条是
"沙盒是全新会话、`Battle.Cooldowns` 为空，t≈0 时十五式一起白得一次出手"。
**实测方向是反的**：第 30 关、60 秒窗口下，冷启动（不预热）**1456**，预热 30 秒后 **1618** —— 冷启动更低。

所以预热期真正的用处不是"去掉起手那一片齐射"（窗口够长时它被摊薄了），
而是**吸收走进射程那段斜坡**：靶子从 `40 + i×100` 一路铺到 1440，玩家站在 `LevelStartX = 80`，
开场那几秒近处的打得着、远处的还在走。20 秒窗下这一点尤其致命（1346 vs 1618，差 17%）。
`ProbeRequest.WarmupSeconds` 的注释按这个结论写。收敛断言也按这个方向钉：
`Warmup/Window = 30/60` 与 `30/120` 相对差 < 5%，且 `0/60 < 30/60`。

## 2. 换掉旧夹具之后，10 / 30 / 60 关的比值

旧夹具是"5 只清一色**地面**青苔妖、`1e8` 无敌血、全摆射程内、无预热、只记 `Effective`"。
换成 `CombatProbe`（真实波次 + 真实血量 + 真实分层 + 预热 30s + 窗口 60s + seed 42 + 饱和供给）之后：

| 关卡 | 逐拍模拟 ÷ 期望模型 |
| --- | --- |
| 10 | **0.99** |
| 30 | **1.19** |
| 60 | **2.80** |

10 关与 30 关落进容差带 `[0.75, 1.25]`（30 关是贴边上）。**60 关的 2.80 不是夹具问题**，见下。

## 3. 60 关那 2.80 归到哪：模型里的命中密度常数

`LevelCurve.HitsPerCast` 里有两个**与场上敌人数无关的常数**：`PerBlade = 1.2`、`PerTick = 2`。
标定依据是 `skill_values.md` §3 那张 `N_ref` 表，参考波次 = 同时在场约 10 只。
探针把这张表变成了活的（屏上那一列「实测命中 / 次」），实测结果：

| 技能 | 形态 | 实测命中/次（30 关 / 60 关） | 模型 | 倍率 |
| --- | --- | --- | --- | --- |
| `skill_02` | 持续伤害（`PerTick`） | 16.1 / **547.7** | 18.5 | 0.87 / 29.6 |
| `skill_12` | 落点带轰炸 | 24.0 / 57.6 | 14 | 1.71 / 4.11 |
| `skill_06` | 多段 | 9.1 / 23.7 | 6 | 1.51 / 3.94 |
| `skill_18` | — | — / 23.4 | 3.6 | — / 6.50 |
| `skill_01` / `skill_07` / `skill_11` | 单发 / 直线 | 1.0 / 1.0 / 2.9 | 1 / 1 / 3 | **1.00 / 1.00 / 0.97** |

**结论**：单发类与直线类的常数是准的（实测 ÷ 模型 = 1.00）；偏差全部集中在
**"一次出手打一片、且持续打"**的形态上，而且**随场上密度增长**——`PerTick = 2` 是个定值，
真实命中数却正比于站在那里挨打的目标数。这就是 60 关 2.80 的全部来源。

**这不是"修一个数"能解决的**：正确做法是把密度常数改成**场上目标数的函数**
（或用真实的密度估计），而那要先有 Phase 1 的期望参悟练度（否则改了也不知道该对到哪）。
本文档 §56 的 `PerBlade / PerTick` 标定依据 `N_ref` 表**应当按这个结论重标**。

## 4. 两条纪律是从 `GameSession` 的步进顺序里逼出来的

- `Effective + Overkill` 才是标称伤害。只记 `Effective` 会把每一次过量击杀静默扣掉——
  而模型侧的 `Dps` 是"输出上限"，两边不是同一个量。
- 出手计数**不能**抄 `UI/Audio.cs` 的「冷却 0 → 正」。冷却衰减写的是 `Math.Max(0, cd − dt×factor)`，
  而 `CastSkills` 在同一个 `Step` 的后半段就把冷却写回正值——从 `Step` 之后观察，那个 0 永远看不到
  （实测第 30 关只认出一式技能）。改用"冷却涨上去"。也**不能**比 `Effects` 集合差：
  增益类法术走 `CastBuff`，根本不产生 `CombatEffect`，差集法会永久漏掉它们。

---

> **改这份文档前请先读 §2.1 与 §63。**
> 其余章节都可以讨论，那两条是这个工具存在的理由。
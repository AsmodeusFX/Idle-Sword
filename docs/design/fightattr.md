# 战斗属性与 DamageModifier 规范

> 状态：**已确认规则**。本文定义 `fightattr.csv` 的职责边界、属性的投放与叠加规则、
> 以及"什么该是属性、什么该是 `DamageModifier`"的判断流程。
>
> 伤害公式与结算顺序归 [combat.md](combat.md)；刻度与关卡曲线归 [balance_ttk.md](balance_ttk.md)；
> CSV 列与字段字典归 [../data/fields.md](../data/fields.md)。
>
> 本文由《土豆修仙》属性规范（V1.0，一份外部产出的草案）**改写并入**（2026-10-07）；那份原文随后已删除。
> 改名、补列、以及与工程现状对齐的地方见第 6 节。

## 1. 三层概念，职责不许混

```
FightAttr            角色**长期拥有**多少属性（各养成系统的投放结果，静态）
    ↓
DamageModifier       这一笔伤害**因为什么条件**获得额外修正（动态、按笔）
    ↓
RuntimeBattleState   角色/目标**现在**处于什么状态（HP、冷却、buff 剩余、当前目标…）
```

一句话对照：

| 概念 | 回答 |
| --- | --- |
| FightAttr | 「我长期拥有什么」——`攻击力 = (atk_base + 武器) × (1 + atk_percent) + 平攻` |
| DamageModifier | 「这一笔为什么不一样」——目标残血 ×2、目标带状态 ×1.3、BUFF 窗口 ×1.5 |
| RuntimeBattleState | 「现在怎么样」——`HP = 700/1000`、`skill_01 冷却 2.3s`、当前目标是谁 |

**`MaxHP` 是 FightAttr（由属性算出），`HP` 是 RuntimeBattleState。** 前者进属性面板，后者只活在对局里。

## 2. `fightattr.csv` 的职责

它是**所有属性投放系统的标准属性索引表**，不只是"给战斗系统查数值"。

- 修行星图、剑诀、铸造/淬炼、参悟、剑灵——凡是要给角色加属性，一律引用这里的 `id`；
- 编辑器/工具从这张表取可选属性列表，**不许各系统自己维护一套属性 id**
  （`TalentSystem.talent_atk` / `EquipmentSystem.equip_attack` 那种写法见过一次就够了）；
- 属性面板（GM →「属性面板」）**按表逐行渲染**，加一行属性会自动出现在面板上。

### 2.1 列的含义

| 列 | 含义 |
| --- | --- |
| `id` | 属性 id，snake_case。**改名等于改接口**：代码、天赋效果、文档、存档校验都可能引用它 |
| `name` | 面板显示名 |
| `format` | 面板渲染方式（`integer` / `percent` / `decimal`）。**全工程只有属性面板读它，且加载期不校验**，未知取值兜底成普通数字 |
| `base_value` | 基础值（"裸角色"的值，不含任何养成投放） |
| `min_value` / `max_value` | 这个属性**合计值**的上下限。`max_value` **留空 = 不设上限** |
| ~~stack~~ | **没有这一列**：本轮全表都是"加算"，写成列等于一个恒定的格子。将来真出现"取最大"或"乘算"类属性时再加，并同时改加载期校验 |

上下限管的是**合计值**（基础 + 各来源加成之后），不是基础值：

- 加载期校验 `base_value` 落在区间内；
- 运行时由 `GameSession.ClampAttr` 夹**合计值**，所以**增益也吃同一条上限**
  （例：`skill_cdr` 基础已经顶到 1，仙风云体术再给 +25% 也只会被夹回 1）。

> ⚠️ 上限是**防手滑的护栏**，不是平衡旋钮。要调平衡请改属性的持有者（节点、装备、技能），
> 不要把上限往下压：超出部分会**静默消失**，而面板上显示的还是夹取之前的值。

`min_value` 同时承担了从前那份硬编码名单的职责——"哪个属性不能为 0"写在它自己那一行上：

- `basic_interval` / `basic_power` / `basic_range` / `melee_range` / `melee_stop_range` 的 `min_value = 0.01`
  （取 0 分别意味着"普攻永不出手""普攻零伤害""近战永远够不着"，都不报错、只让一整个形态不能用）；
- `crit_damage` 的 `min_value = 1`（暴击不该比不暴击更弱）。

## 3. 属性清单（当前）

| id | 名 | 基础 | 上限 | 归哪一层 |
| --- | --- | --- | --- | --- |
| `atk_base` | 攻击基础 | 10 | — | DMG1 的 `AttackPower`（与武器相加后进乘区） |
| `atk_percent` | 攻击加成 | 0 | — | DMG1 的 `AttackPower` |
| `crit_rate` | 暴击率 | 0.05 | 1 | DMG2 的**判定** |
| `crit_damage` | 暴击倍率 | 1.5 | — | DMG3 第二乘区 |
| `generic_damage` | 通用增伤 | 0 | — | DMG3 第一乘区（**加算池**） |
| `attack_speed` | 普攻攻速 | 0 | 3 | **频率轴**：`basic_interval ÷ (1+它)` |
| `skill_cdr` | 法术冷却缩减 | 0 | 1 | **频率轴**：`cooldown ÷ (1+它)` |
| `max_hp_base` | 气血基础 | 60 | — | 生存 |
| `max_hp_percent` | 气血加成 | 0 | — | 生存 |
| `dodge` | 闪避 | 0.02 | 1 | 承伤方减伤（**不是**命中/闪避对抗） |
| `move_speed` / `stop_range` / `basic_range` / `melee_range` / `melee_stop_range` | 移动与射程 | — | — | 位置与选敌 |
| `basic_interval` / `basic_power` | 普攻间隔与倍率 | 1 / 1 | — | 普攻的 `SkillRate` 与频率基准 |

### 3.1 `atk_percent` 与 `generic_damage` 的分工（硬规则）

两者**数值上是同一个乘区**（见 [combat.md](combat.md) §4.1），同时存在只为展示口径：

- `atk_percent` = **面板攻击力成长**（玩家在攻击力那一行看得见）；
- `generic_damage` = **伤害成长**（不上面板攻击力）。

> ⚠️ **同一个养成来源只能选一边投**。两边都投 = 同一个效果乘两次，而面板上只显示一半。

### 3.2 按伤害类型分池（机制已定，本轮不投放）

`skill_damage` / `basic_attack_damage` 这类"技能伤害 +X% / 普攻伤害 +X%"将来也进**第一乘区（加算池）**，
只是取值时按 `DamageType` 过滤。机制先定在这里，**本轮不加属性、不加消费者**——真要用时再定 id 与投放。

## 4. `DamageModifier`：条件型倍率

### 4.1 定义

**针对某一笔 DamageEvent，根据条件、状态或事件产生的动态倍率。它不属于角色的永久属性。**

来源可以是 BD、技能、Buff、Debuff、被动、战斗事件——但**最终都必须走同一个结构**：

```csharp
public readonly record struct DamageModifier(string Source, ModifierZone Zone, double Multiplier);
public enum ModifierZone { Generic, Build, Vulnerability }
```

（定义在 `Core/Data/DamageFormula.cs`。列表形态的求值已经就位：
`DamageFormula.GenericMultiplier` / `BuildMultiplier` / `VulnerabilityMultiplier`。）

### 4.2 生成 → 使用

```
BD / 技能 / Buff 逻辑
    ↓ 生成
DamageModifier（source / zone / multiplier / condition）
    ↓ 注册到战斗上下文（**注册表尚未实现**）
DamageEvent
    ↓
DMG3 统一读取 → Build × Vulnerability
```

**当前实施状态**：注册表还没建，只有三个机制按这个结构在 `GameSession.Hit` 里**就地求值**
（共享伤害倍率窗 / 斩杀 / 利用状态，见 [combat.md](combat.md) §4 那张表）。
它们是同一件事的三种条件，结构先行、内容后加。

### 4.3 禁止事项

1. **禁止各系统自己写伤害公式**。不同技能、不同 BD 各写一套 `damage *= 1.5` / `damage *= 1.3`，
   等于把公式拆成十几份，之后再也答不出"这个技能到底多强"。
2. **禁止把条件型增伤做成属性**。`boss_damage` / `burn_damage` / `low_hp_damage` /
   `first_hit_damage` / `fifth_hit_damage` 这类东西一列一列加进 `fightattr.csv`，
   属性表会变成一张条件清单，而"我长期拥有什么"这个问题就再也答不出来。
3. **禁止在 DMG3 之外直接改已经算出的最终伤害**。要么生成 Modifier、要么产生新的 Attack Event
   （额外攻击走 `Launch`，见 [combat.md](combat.md) §8）。
4. **禁止同一个效果两边都投**（`atk_percent` × `generic_damage`，见 §3.1）。

## 5. 新增一个战斗数值时的判断流程

1. **它是角色长期拥有、各养成系统能直接投放的静态属性吗？**
   不是 → 不进 `fightattr.csv`，去看第 3 步；是 → 第 2 步。
2. **它需要被面板展示 / 被编辑器当选项列出来吗？** 需要 → 定义 FightAttr 并加进 `fightattr.csv`
   （同时补 `min_value` / `max_value`、在属性面板加一个 `case`、更新 `../data/fields.md`）。
3. **它是"满足某个条件才对这一笔生效"的倍率吗？** 是 → `DamageModifier`
   （加算语义进 `Generic`，乘算语义进 `Build` / `Vulnerability`）。
4. **它需要判断当前攻击 / 目标 / 状态吗？** 需要 → 优先 `DamageModifier`；
   如果它其实是"多久一次"（频率）或"这一发本来能打多少"（DMG1），回 [combat.md](combat.md) §10 的落位流程。

### 5.1 加一行属性的完整清单（漏一处就会红）

1. `fightattr.csv`：加行，写全 `base_value` / `format` / `min_value` / `max_value`；
2. `Config/Schemas/headers.json`：只在**列**有变化时才要改；
3. `UI/AttributePanel.cs` 的 `switch (id)`：不加 `case` 也能渲染出那一行，但**「受什么影响」会是空白**
   ——**冒烟测不出来**（它比的是行数），只能靠 `--capture` 目视；
4. `../data/fields.md`：字段字典里补这一条；
5. `GameSession`：让某个公开属性读它（`atk_base` 那样），别在战斗代码里到处 `Config.Attr`。

## 6. 与那份外部规范的差异（并入时逐条对账）

| 外部规范怎么写 | 本工程怎么做 | 为什么 |
| --- | --- | --- |
| `atk_base` / `max_hp_base` / `crit_rate` / `attack_speed` / `skill_cdr` / `generic_damage` | **照改名**（本轮已改） | 命名成对、一眼看得出哪个是基础值 |
| `max_hp_percent` | 照改（原 `hp_percent`） | 与 `max_hp_base` 成对 |
| `atk_base × (1 + atk_percent)` | `(atk_base + 武器) × (1 + atk_percent) + 天赋平攻` | 武器在乘区内、平攻在乘区外，工程现状如此，写死在这里 |
| `fightattr.csv` 只写了 `id/name/base_value/format` 类字段 | 补 `min_value` / `max_value` | 原 `config_plan.md` 就要求"上下限与叠加规则"；上限要真的能夹住 |
| 说"编辑器应读 fightattr.csv 生成属性列表" | 目前只有**属性面板**按表渲染，**配置编辑器还没有** | 别把计划当成已实现 |
| 加了 `stack`（叠加规则）的暗示 | **不加列**：本轮全表都是加算，加一列恒定值不携带信息 | 真出现取最大/乘算类属性时再加并同时改校验 |
| `SkillFlat` 当成常规项 | 只留列、全表 0，并写明陷阱 | 见 [combat.md](combat.md) §2 |

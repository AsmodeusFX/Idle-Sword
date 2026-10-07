# 配置表规划

状态：已生成全部指定表与辅助表，**共 23 张加载**（以 `GameConfig.Files` 为准），另有 3 张技能退役库（`SwordSkill_Retired.csv` / `SkillEffect_Retired.csv` / `SkillBuff_Retired.csv`）**不加载**。实际字段见 `fields.md`，复杂多货币成本/效果组合表仍为后续扩展方案。

配置统一放在 `idle-sword/Config/Tables/`。UTF-8 编码，读取兼容 BOM；首行为英文 snake_case 字段名，中文解释在字段字典维护。稳定 ID 作为引用依据，不使用名称作为关联键。

| 文件 | 职责 |
| --- | --- |
| monster.csv | 普通怪、精英、BOSS 和裂隙模板；基础 HP/ATK、移速、攻击距离、攻击间隔、攻击行为、可移动/可攻击标志、表现与掉落引用 |
| level.csv | 关卡 ID、名称、顺序、长度/格数、普通/精英/BOSS 各自 HP/ATK 倍率、刷怪点组、BOSS/裂隙 ID、首次/重复奖励引用 |
| item.csv | 各种货币、材料和道具定义 |
| fightattr.csv | 角色属性定义、基础值、上下限、显示方式与叠加规则；包含默认移动速度 |
| SwordLevel.csv | 5 个境界、默认解锁状态、前置、消耗组和关联技能 |
| SwordSkill.csv | 释放控制层（CD / 射程 / 境界 / 成长 / 释放条件 / 选敌 / 打击层 + 引用 Effect 与 Buff）。**不描述"放出去之后发生什么"**，只管"能不能放、放谁" |
| SkillEffect.csv | 技能的一次性战斗行为：释放路径的出手（`attack`）/ 定时自身出手（`auto_attack`，环绕飞剑）/ 缩短冷却（`shorten_cooldown`）/ 影分身复制（`mirror_cast`）。形态、范围、条件倍率（斩杀 / 利用状态）与"命中时挂哪份状态"（`buff_id`）都在这 |
| SkillBuff.csv | 技能的时间化状态（11 种 `kind`）：敌人向的 `dot` / `slow` / `chill` / `stun` / `vulnerable`，自身向的 `haste` / `crit_reduce` / `shield` / `regen` / `lifesteal` / `mirror` |
| SkillBuffTimeline.csv | Buff 的周期触发：到点执行一条 `SkillEffect` 行（目前只有环绕飞剑 `on_tick`） |
| SkillBuffTrigger.csv | Buff 的事件触发：战斗事件发生时执行一条 `SkillEffect` 行（目前只有 `on_crit` / `on_skill_cast`） |
| Talent.csv | 天赋节点、行列位置、等级、消耗、效果 |
| Equip.csv | 武器模板、基础属性、品质、打造与词条池引用；实际武器实例写入存档 |
| SwordUpgrade.csv | 4 类参悟强化项、目标技能/效果字段、等级上限、加算/乘算规则、消耗 |
| Pet.csv | 剑灵模板、形态、属性、技能组 |
| PetSkill.csv | 剑灵行为、目标、冷却和效果 |
| PetEquip.csv | 剑灵增强 Buff 模板、类别、效果、装备限制 |

> **为什么技能侧的表叫 `SkillEffect.csv` 而不是 `effect.csv`**：本文件在「建议辅助表」里早就把一个
> `effect.csv` 用作**天赋 / 解锁效果**的提议名（属性、技能参数与功能解锁效果）。技能拆成三层之后，
> "技能打出去的那一次行为"也需要一张表；若也叫 `effect`，**两件完全不同的事会共用同一个名字、造成永久混淆**
> ——所以技能侧的表一律带 `Skill` 前缀：`SkillEffect` / `SkillBuff` / `SkillBuffTimeline` / `SkillBuffTrigger`。

## 建议辅助表

| 文件 | 职责 |
| --- | --- |
| spawn_point.csv | 刷怪点 ID、所属组、格号/位置、激活规则、波次引用；越过位置停刷 |
| wave.csv | 刷新间隔与精英节奏；支持前波存活时继续刷新 |
| wave_unit.csv | 一条波次刷哪些怪、各刷几只（混编，见 `fields.md`） |
| drop.csv | 奖励组、物品、数量、概率、首杀/重复调用 |
| cost.csv | 单次或按等级的多货币消耗 |
| effect.csv | 属性、技能参数与功能解锁效果（**提议名，尚未落地**）。⚠️ **与在役的 `SkillEffect.csv` 不是一回事**——后者是"技能的一次性战斗行为"，与这里的"天赋 / 解锁效果"只是碰巧都叫 effect |
| TalentLayout.csv | 修行星图的格子坐标与前置连线（**归节点编辑器所有**，见 `fields.md`） |
| contemplation.csv | 参悟对象、点击规则、产物组、自动参悟周期、积存容量及解锁引用 |
| game_settings.csv | 全局默认规则与可调参数 |

刷怪点在角色进入所属格时激活，角色越过该点后停止刷新；远处未进入的格不提前刷新。每格刷怪点数量和准确位置由配置决定。

## 灵核投放约束（已确认）

- 每个关卡的 BOSS 首杀固定发放 1 个灵核，重复击杀发放 0 个。
- `level.csv` 的首次与重复奖励分别引用奖励组；灵核只允许通过关卡 BOSS 首杀奖励投放。
- 普通掉落、精英掉落、BOSS 通用掉落、裂隙、参悟、抽取等可重复来源不得直接或间接投放灵核。
- 校验各关首杀奖励汇总后恰好包含 1 个必得灵核，不允许随机数量、概率掉落或多处叠加投放。
- 校验重复奖励与其他产出引用链不含灵核，避免配置新增来源破坏有限投放。
- 首杀资格按稳定关卡 ID 保存，即使多个关卡使用同一怪物模板也独立结算。
- 玩家存档记录已领取首杀奖励的关卡集合；关卡排序变化不改变资格，扩展新关卡产生新的首杀资格。
- 累计已投放灵核数等于已领取首杀奖励的关卡数，不等于消耗后的背包余额。

## 裂隙特别约定

- 裂隙模板仍在 monster.csv，不另写一套生命值和受击逻辑。
- 单位角色类型独立表达为裂隙；由关卡状态决定是否可选中与受伤。
- 裂隙不移动、不攻击。是否受普通/精英/BOSS 强度倍率影响需要单独定义；建议 level.csv 配置裂隙 HP 倍率。
- 首杀标记、当前 HP、刷怪点是否越过等运行状态不回写 CSV。

## 校验要求

- 重复 ID、缺失外键、非法类型、负时间、非法概率、天赋行号超过 5 等均报告文件、行号、字段。
- 对技能类型和效果参数使用明确枚举或已注册类型，不从 CSV 执行任意代码。
- **技能五表**（`SwordSkill` / `SkillEffect` / `SkillBuff` / `SkillBuffTimeline` / `SkillBuffTrigger`）额外校验：
  - **外键**：`effect_ids` / `buff_ids` / 效果行的 `buff_id` / 时间轴与触发器的 `buff_id`·`effect_id` 都必须能解析到真实的行；
  - **枚举白名单**：`kind` / `effect_type` / `condition` / `buff.kind` / `buff.target` / 时间轴 `trigger` / 触发器 `trigger_type`，未知取值直接拒；
  - **"无关列必须为默认值"的逐类型校验**：每个 `effect_type` 只允许它自己那几列非零（其余必须为空 / 0 / 1），防止"配了但不生效"的死配置；
  - **纯派发标记不带参数**：`shorten_cooldown` / `mirror_cast` 的每一列都必须是默认值。
  - 规则与实现同在 `Core/Data/SkillTable.cs`；退役库用**同一套解析器**读通（防它与表结构悄悄分叉）。
- 时间使用秒；位置、射程与移动速度明确采用同一逻辑坐标体系，速度单位为逻辑单位/秒。
- 配置值与占位调试值明确区分；100 关配置存在不等于已完成数值平衡。

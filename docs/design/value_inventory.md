# 战斗价值清单（战斗底盘可投放的价值门类）

> 状态：**当前底盘快照**。用途只有一个——Ver2.0 **Part.1** 往各养成系统分发价值时，**对照这份清单说得出落点**（见 [design_lens.md](design_lens.md) 第五节第 3 问）。
> **不写配置数值**：数值会随 `balance_ttk.md` 的对齐轮变动，这里只写「有没有这个维度、锚在哪」。
> 代码锚点默认在 `idle-sword/Features/Battle/GameSession.cs`，其余写全路径。

怎么读「性质」这一列：**数值** = 纯数字增减；**形态** = 玩家能看见的变化（弹数、弹道、范围）；**状态** = 作用在敌人或自己身上的持续效果；**开关** = 改变规则本身（有无某能力）；**资源** = 非伤害产出。

---

## A. 输出（伤害）

| 维度 | 性质 | 锚点 |
| --- | --- | --- |
| 基础攻击 | 数值 | `fightattr.atk` → `GameSession.Attack` |
| 百分比攻击 | 数值 | `fightattr.atk_percent` + 天赋 `effect=atk`（`TalentBonus("atk")`） |
| **固定攻击** | 数值 | 天赋 `effect=atk_flat`，**加在乘区之外**（过渡期主投这一种） |
| 武器攻击 | 数值 | `Equip.base_atk × (1 + .15×WeaponLevel) × WeaponRoll`，`Features/Forging/ForgingSystem.cs` |
| 技能威力 | 数值 | `SkillDef.Power × (1 + skill_level_bonus×(rank−1) + SkillBonus(id,"damage_percent"))` |
| 共享伤害倍率窗 | 数值 | `_buffPower` / `_buffTime`（`power != 1` 的 buff 写入） |
| 处决（斩杀增伤） | 数值 | `secondary=execute` |
| 对状态目标增伤 | 数值 | `secondary=bonus_vs_state` |
| 易伤（敌方承伤放大） | 状态 | `secondary=vulnerable`（**当前无在役技能**） |
| 宠物伤害 | 数值 | `PetSkill.power` + `PetEquip.power`，`Features/SwordSpirit/SwordSpiritSystem.cs` |
| 召唤物 | 形态 | `Kind=summon`（**当前无在役技能**） |

## B. 节奏（攻速 / 冷却）

| 维度 | 性质 | 锚点 |
| --- | --- | --- |
| 攻速 | 数值 | `secondary=haste` → `HasteFactor`，加速**非 buff 类**冷却与剑灵，**不加速 buff 类法术** |
| 冷却缩减 | 数值 | `SkillDef.Cooldown` + buff 类随等级的覆盖率成长（`BuffCooldown`） |
| **暴击缩冷却** | 机制 | `secondary=crit_reduce` 的 `secondary_extra`：暴击后缩短一个非增益、非神通法术的冷却（`CritShortenCooldown`） |
| 普攻间隔 | 数值 | `fightattr.basic_interval` |
| 多发错时 | 形态 | `ProjectileCount` / `VolleyInterval` / `VolleyJitter`（多发本身提高单位时间伤害） |

## C. 暴击 / 闪避

| 维度 | 性质 | 锚点 |
| --- | --- | --- |
| 暴击率 | 数值 | `fightattr.crit` + `CritBonus`（`crit_reduce` 的绝对值叠加） |
| 暴击倍率 | 数值 | `fightattr.crit_damage`（全游戏**唯一**的暴击判定点在 `Launch`） |
| 闪避 | 数值 | `fightattr.dodge`（`HurtPlayer`） |

> ⚠️ **没有命中率 / 精准，也没有敌方闪避**——"打不打得中"这个维度在本底盘里不存在。

## D. 弹道形态与覆盖

| 维度 | 性质 | 锚点 |
| --- | --- | --- |
| 弹射数量 | 形态 | `ProjectileCount` |
| 多目标（历史形态） | 形态 | `secondary=multi`（**当前无在役技能**） |
| 恒穿透 | 形态 | `trajectory=line_pierce`，或 `secondary=pierce`（后者无在役技能） |
| 概率穿透 | 形态 | `pierce_chance`（仅 `line_shot`，首次命中判定一次） |
| 范围伤害 | 形态 | `aoe_radius`（`ground` 有默认值，`sky_drop` 必须为正） |
| 全体命中 | 形态 | `aoe_all` |
| 落点 / 带式铺开 | 形态 | `spread` / `band` |
| 攻击距离 | 数值 | `SwordSkill.range`（决定停步与可达性）；普攻 `basic_range` / `melee_range` |
| 分层命中 | 开关 | `hits`（ground / air / both）——飞行单位免疫 `ground` |
| 选敌偏好 | 机制 | `targeting`（最近 / 最高血 / 最低血 / 最前） |

## E. 异常状态（施加于敌方）

| 维度 | 性质 | 锚点 |
| --- | --- | --- |
| 减速 | 状态 | `secondary=slow` |
| 寒冷 | 状态 | `secondary=chill`（减速 + 变色） |
| 眩晕 | 状态 | `secondary=stun`（无法移动与攻击） |
| 灼烧 DOT | 状态 | `secondary=dot`（按 tick 跳伤，**归因到施加技能**）——当前唯一的"燃烧"形态 |
| 易伤 | 状态 | `secondary=vulnerable`（当前无在役技能） |
| 全屏定身 | 状态 | `cast_root`（**不算 secondary**，复用 `StunUntil`） |

## F. 控场与位移

| 维度 | 性质 | 锚点 |
| --- | --- | --- |
| 击退 | 机制 | `knockback`（每次命中推离玩家） |
| 吸附 / 聚怪 | 机制 | `gather`（朝效果中心拉近；与击退反向且可并存） |

## G. 生存

| 维度 | 性质 | 锚点 |
| --- | --- | --- |
| 气血上限 | 数值 | `fightattr.hp` + 天赋 `hp`（百分比）/ `hp_flat`（固定，乘区外） |
| 护盾 | 数值 | `secondary=shield`（吸收量按攻击折算）+ `_shield` / `_shieldUntil` |
| 护盾期环绕飞剑 | 机制 | `TickSwordGuard`（参数 `guard_blade_power` / `guard_interval` / `guard_range`） |
| 回血 | 数值 | `secondary=regen`（**当前无在役技能**） |
| 吸血 | 数值 | `secondary=lifesteal`（**当前无在役技能**） |
| 闪避 | 数值 | 见 C 组 |

## H. 资源与掉落

| 维度 | 性质 | 锚点 |
| --- | --- | --- |
| 灵石掉落 | 资源 | `monster.gold`（击杀必得） |
| 掉落固定 +N | 资源 | 天赋 `effect=drop_flat`（只加在怪物自身那一笔；**排除裂隙**，也**不含** `drop.csv` 奖励组） |
| 关卡奖励组 | 资源 | `drop.csv`（首杀组 / 重复组） |
| 灵核 | 资源 | **只由 BOSS 首杀发放**，唯一入账点在 `GameSession.HurtEnemy` |
| 参悟货币 | 资源 | `contemplation.csv`（点击 / 自动产出） |

> ⚠️ **没有经验 / 等级**。成长全部通过货币购买（修行 / 法术 / 铸造 / 参悟 / 剑灵），不存在 XP 属性。

## I. 元维度（改变规则本身）

| 维度 | 性质 | 锚点 |
| --- | --- | --- |
| 技能等级 | 数值 | `Features/SwordRealm/SwordRealmSystem.cs`，`skill_level_bonus` |
| 境界解锁 | 开关 | `SwordLevel.csv`（每境若干技能） |
| 参悟强化 | 数值 | `SwordUpgrade.csv` 的 `effect` 词汇表（目前 `damage_percent` / `inherit_percent`） |
| 影分身 | 机制 | `secondary=mirror`（分身复制本体每一式，比例不封顶） |
| 普攻形态切换 | 开关 | 天赋 `ranged_basic`（近战 → 远程） |
| 自动出手 | 开关 | 天赋 `auto_basic`（省手，**不增加 DPS**——与手动共用同一冷却键） |
| 神通触发 | 机制 | `trigger_chance` + `trigger_chance_step`（只在普攻出手时摇） |
| 系统解锁 | 开关 | 天赋 `*_system` → `Core/State/Systems.cs` 的 `Systems.ByEffect` |

---

## J. 当前**不存在**的维度（要投放就得先做底盘）

| 缺什么 | 说明 |
| --- | --- |
| 命中率 / 精准 | 不存在，也没有敌方闪避 |
| 防御 / 护甲 / 减伤 | 不存在。生存只有「气血 + 护盾 + 闪避」三条 |
| 元素属性与克制 | 不存在。异常状态只有 5 种（减速 / 寒冷 / 眩晕 / 灼烧 / 易伤） |
| 射程加成 | 不存在。射程只由"学了哪个技能"决定 |
| 弹速加成 | 不存在。`speed` 是技能自身配置，不是可成长属性 |
| 掉落倍率（百分比） | 不存在。只有固定值 `drop_flat` |
| 经验 / 等级 / 天赋点 | 不存在 |
| 敌方增益 / 自己上 debuff | 不存在。状态只朝一个方向走 |

## 战斗公式（从代码逐条抄的，不是复述）

> 这一节是 `GameSession` 的**现状快照**，每条都带锚点，改公式时**必须同步改这里**——
> 与 `data/fields.md` 末尾那节「当前公式与全局设置」是同一个口径（那边偏配置参数，这边偏结算形状）。

| 量 | 公式 | 锚点 |
| --- | --- | --- |
| **攻击** | `(fightattr.atk + 武器攻击) × (1 + atk_percent + 天赋atk) + 天赋atk_flat` | `GameSession.Attack` |
| 武器攻击 | `Equip.base_atk × (1 + 0.15 × 淬炼等级) × WeaponRoll` | `GameSession.WeaponAttack` |
| **气血** | `fightattr.hp × (1 + hp_percent + 天赋hp) + 天赋hp_flat` | `GameSession.MaxHp` |
| **法术威力** | `SwordSkill.power × (1 + skill_level_bonus × (技能等级−1) + 参悟damage_percent)` | `GameSession.SkillPower` |
| 普攻伤害 | `最终攻击 × fightattr.basic_power`，间隔 `basic_interval` | `TickBasicAttack` → `Launch` |
| **暴击** | `rand < fightattr.crit + CritBonus` → `伤害 × fightattr.crit_damage` | `Launch`（**全游戏唯一判定点**，逐弹丸各摇一次） |
| 共享伤害倍率 | 增益窗口内 `伤害 × _buffPower` | `Launch` |
| **易伤** | 目标 `VulnerableUntil > 0` → `伤害 × VulnerableFactor` | `HurtEnemy`（**记账发生在这之后**） |
| 落地 | `Hp = max(0, Hp − 伤害)`；有效 = `before−after`、溢出 = `伤害−有效` | `HurtEnemy` |
| **冷却流逝** | `cd = max(0, cd − dt × HasteFactor)`，**buff 类不吃加速** | `Step` 顶部 |
| 攻速 | `HasteFactor`（`secondary=haste` 窗口内 = `1 + value`，否则 1）；同时作用于**普攻冷却键**与剑灵 | `GameSession.HasteFactor` |
| 掉落 | `monster.gold + 天赋drop_flat`（**裂隙除外**；且**不含** `drop.csv` 奖励组） | `HurtEnemy` |
| 射程 / 停步 | 取已习得**非 buff** 法术的最大射程；一个都没学时回落到 `BasicAttackRange`（**跟着近战/远程形态走**） | `GameSession.AttackRange` |

**几条值得记住的形状**（改公式时最容易碰坏的）：

- **平攻 / 平血加在乘区之外**——刻意的：并进乘区的话，后期会被装备与百分比放大成完全不同的量级。
- **暴击只有一处判定**（`Launch`）：自建一条命中路径会**静默丢掉暴击**，近战那次就是为此才没走"瞬时命中"的捷径。
- **易伤在 `HurtEnemy` 里、记账之前**：在 `Hit` 里读 `damage` 会漏掉 `VulnerableFactor`，伤害统计会偏小。
- **攻速不加速 buff 类**：否则仙风云体术会在持续期内就转好，等于自己给自己减冷却（100% 常驻）。
- **射程的回落必须跟着普攻形态走**：近战只有 150，若回落到远程的 950，角色会停在"以为够得着"的地方——
  **裂隙打不掉、整关卡死**。

> **DPS / TTK 的模型口径**不在这里，在 [balance_ttk.md](balance_ttk.md)（§2 刻度、§2.1 前期模型、
> §6.2 改数值前的三步走）。改数值前先读那份。

## K. 死配置（代码与词汇表都在，只差载体）

当前**没有任何在役技能**使用的值：次级效果 `pierce` / `multi` / `regen` / `lifesteal` / `vulnerable`；形态 `hover_homing`；类别 `summon`；`hits=air`。

退役技能原文收在 `idle-sword/Config/Tables/SwordSkill_Retired.csv`（**不参与加载**）。这些是为后续扩展留的词汇表——`tests/Program.cs` 用**内存改配置**维持它们的覆盖，所以别当成死代码删掉。

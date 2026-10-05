# CSV 字段字典

所有配置均为 UTF-8，首行英文表头，ID 大小写敏感。时间单位秒，1屏=1920逻辑单位，概率与百分比用小数。原始CSV由自定义加载器读取，Godot导入方式为keep。

以下为当前真实字段；并非最终技能/锻造设计。`Config/Schemas/headers.json`保存表头清单，类型与业务约束在`Core/Data/GameConfig.cs`执行。

## monster.csv

| 字段 | 类型 | 含义 |
| --- | --- | --- |
| id | string | 稳定唯一 ID |
| name | string | 中文显示名称 |
| kind | enum | 类别；对应表的已注册行为/分类 |
| layer | enum（可空） | 所在层：`ground` / `air`，空 = `ground`。**`air` 的单位免疫定位为 `ground` 的技能**，见下方「层与定位」。 |
| hp | number | 基础生命值 |
| atk | number | 基础攻击力 |
| attack_range | number | 攻击距离（逻辑坐标） |
| attack_interval | number | 攻击间隔（秒，大于0） |
| move_speed | number | 移动速度（逻辑单位/秒） |
| attack_type | enum | melee / ranged / magic / none |
| gold | number | 击杀必得灵钱 |
| visual | string | Assets/visuals.json 中的表现 ID |

## level.csv

| 字段 | 类型 | 含义 |
| --- | --- | --- |
| id | string | 稳定唯一 ID |
| name | string | 中文显示名称 |
| order | integer | 排序；关卡顺序不得重复 |
| cells | integer | 关卡格数，当前25 |
| normal_hp | number | 普通怪 HP 倍率 |
| normal_atk | number | 普通怪 ATK 倍率 |
| elite_hp | number | 精英 HP 倍率 |
| elite_atk | number | 精英 ATK 倍率 |
| boss_hp | number | BOSS HP 倍率 |
| boss_atk | number | BOSS ATK 倍率 |
| rift_hp | number | 裂隙 HP 倍率 |
| wave_id | reference | wave.id |
| boss_id | reference | monster.id，分类必须boss |
| rift_id | reference | monster.id，分类必须rift |
| first_reward | group | drop.group_id，首杀组；恰好含1个妖核 |
| repeat_reward | group | drop.group_id，重复组；不得有妖核 |

## item.csv

| 字段 | 类型 | 含义 |
| --- | --- | --- |
| id | string | 稳定唯一 ID |
| name | string | 中文显示名称 |
| kind | enum | 类别；对应表的已注册行为/分类 |
| description | string | 说明 |

## fightattr.csv

| 字段 | 类型 | 含义 |
| --- | --- | --- |
| id | string | 稳定唯一 ID |
| name | string | 中文显示名称 |
| base_value | number | 属性基础值；百分比用0～1表示 |
| format | enum | 显示提示 integer / percent / decimal |

其中 `basic_interval`（普攻间隔，秒）、`basic_power`（普攻倍率，乘最终攻击）、`basic_range`（普攻射程）三行是**普通攻击**的参数，校验为**必须大于 0**（其余属性允许为 0）。普攻的规则见 [../design/core_rules.md](../design/core_rules.md) 的「战斗」一节。

## SwordLevel.csv

| 字段 | 类型 | 含义 |
| --- | --- | --- |
| id | string | 稳定唯一 ID |
| name | string | 中文显示名称 |
| default_unlocked | bool | 0或1 |
| cost_gold | number | 灵钱消耗；天赋按当前等级+1乘此值 |
| order | integer | 排序；关卡顺序不得重复 |

## SwordSkill.csv

| 字段 | 类型 | 含义 |
| --- | --- | --- |
| id | string | 稳定唯一 ID |
| name | string | 中文显示名称 |
| realm_id | reference | SwordLevel.id |
| kind | enum | 类别；对应表的已注册行为/分类 |
| cooldown | number | 独立冷却秒数 |
| range | number | 目标选择距离（逻辑坐标） |
| power | number | 伤害/效果倍率 |
| duration | number | 持续时间秒数；定点技能为延迟；自定义飞行形态下为飞行／下坠时长 |
| max_level | integer | 最大等级 |
| cost | number | 基础升级成本；具体成长规则见下方 |
| cost_growth | number | 剑诀升级消耗指数倍率 |
| description | string | 技能说明文字，用于界面展示 |
| secondary | enum（可空） | 次级效果：pierce / multi / slow / stun / dot / vulnerable / chill / lifesteal / execute / shield / regen / haste / crit_reduce / mirror / bonus_vs_state；空表示无。其中 pierce / multi / shield / regen 当前没有技能使用（见下）。`mirror`（影分身）另有三条约束：必须 `kind = buff`、`secondary_duration > 0`、`secondary_value ≤ 1`；`bonus_vs_state`（利用状态）要求 `secondary_value > 0` |
| secondary_value | number | 次级效果强度（multi 为目标数；stun / pierce 可忽略） |
| secondary_duration | number | 次级效果持续时间（秒）；瞬发 / 参数类可为 0 |
| aoe_radius | number | 范围半径（ground 类）；0 表示单体，默认沿用 220。sky_drop 形态下为落点判定半径，此时必须为正（不回退 220） |
| trajectory | enum（可空） | 飞行形态：bolt / line_shot / line_pierce / sky_drop / arc_homing / hover_homing；空表示 bolt（直线弹道）。仅 projectile 可用 |
| projectile_count | integer | 单次出手的弹数，至少 1；非 projectile 只能为 1 |
| hover_time | number | **起飞前在空中停留的秒数**：hover_homing 为悬浮蓄势、sky_drop 为浮现后停留、line_pierce 为浮现；0 表示不停留 |
| arc_min | number | 弧高随机下界（arc_homing 专用），设计坐标的垂直振幅（非角度）；**允许为负，负值表示从下方掠过**，下限 -60 |
| arc_max | number | 弧高随机上界；不得超过 300（弧顶会越出战斗画面） |
| speed | number | 弹道速度（逻辑单位/秒）；0 表示默认 1500。**只作用于剑诀**，宠物弹与召唤弹始终走默认值 |
| spread | number | 弹群排列间距：sky_drop 为落点横向间距（**参与判定**，由 Core 算进 X），其余形态为表现层的排列间距 |
| volley_interval | number | 多发之间的发射间隔（秒），第 i 支额外延迟 i × interval；0 表示同时发射 |
| volley_jitter | number | 在发射间隔之上叠加的 ±随机（秒），让出剑时机不整齐 |
| spawn_jitter | number | 出生高度的随机幅度（像素，仅表现；随机值由 Core 摇定一次） |
| pierce_chance | number | **首次**命中时的穿透概率（0～1，0 表示不穿透）；只对 `line_shot` 有意义，且一次判定机会用完即关闭 |
| secondary_extra | number | 次级效果的附加参数：当前用于 `crit_reduce` 的每次缩短秒数 |
| trigger_chance | number | **触发概率（0～1）**。`0`＝沿用「冷却到点自动释放」；`> 0`＝不再自动释放，改为每次**普通攻击出手时**按此概率触发。冷却仍是硬门槛（即「最短触发间隔」）。见下方「触发概率（真诀）」。 |
| trigger_chance_step | number | **触发概率的逐次累加量**（0 = 不累加，保持固定概率）。`> 0` 时当前概率 = `min(1, trigger_chance + 已累加值)`：每经过一次普攻且**没摇中**就加上它，**摇中后清零**回到起步值。只在冷却就绪时才累加（与「冷却就绪才可能触发」同口径）。用途是把"看脸"换成"越打越近"：既不会冷却一好就放，也不会连十几次摇不中。 |
| cast_root | number | **施放瞬间的全屏定身秒数**，0 = 无。命中所有合法敌人，**不看射程**；裂隙除外（它本来就不移动、不攻击，而且 `TickEnemies` 只在非裂隙敌人上衰减状态，给它挂定身会永远减不掉）。与 `hover_time` 无关：`hover_time` 是该效果自己的起飞延迟，两者都配 1 只是让"定住一秒再放剑气"读起来顺。 |
| knockback | number | **每次命中把目标沿背离玩家的方向推开的逻辑距离**，0 = 无。裂隙不可移动，永不被推。 |
| targeting | enum（可空） | 选敌方式。空 / `nearest` = 最近的合法目标（受 `range` 限制，现行行为）；`highest_hp` = **全场血量最高者、无视射程**（斩鬼神）；`lowest_hp` = **射程内血量最低者**（青元剑芒的收割——注意它与 `highest_hp` 不同，**仍受射程约束**）；`farthest` = **射程内最靠前的那只**（焚天剑诀的落点：怪从右边来，落在最靠前的位置等于铺在"迎宾位"，后面进来的都要穿过去；落在脚边那只身上则几乎立刻被走过，收益最低）。多发技能用 `lowest_hp` / `farthest` 时按对应顺序取前 n 个，**敌人不足时循环重复打同一个**，所以场上只有一只时几段全打在它身上。同血量 / 同距离 / 同 X 按 Id 升序，自检才有确定结果。 |
| aoe_all | bool | 1 = 该效果落地/范围结算时**命中全体合法敌人**，`aoe_radius` 的判定作用被忽略（仍用于表现层的落点预警圈）。与「天降火海」互斥（校验会拒绝同配）。 |
| hits | enum（可空） | 能打到**哪一层**：`ground` / `air` / `both`，空 = `both`。与 `monster.layer` 配对，见下方「层与定位」。 |
| gather | number | **每次命中把目标朝本效果的中心拉近的逻辑距离**（0 = 不吸），见下方「吸附」。是 `knockback` 的反向孪生：一个推离玩家、一个吸向效果中心，两者都是正交旋钮、不占 `secondary`，所以「吸 + 减速」能同时挂在一个技能上（12 寒冰龙卷）。 |
| band | number | `sky_drop` 的**黑洞铺开宽度**（0 = 沿用默认的"以阵心为中心对称铺开"），见下方「各锁一敌」。> 0 时**每支各锁一个目标、落在它身上**，而天上那排黑洞以**目标群的中轴**为心铺开 `band` 宽（不超过 1400，否则夹不进画面） |

次级效果分两类：状态类（slow 减速 / stun 眩晕 / dot 灼烧 / vulnerable 易伤 / chill 寒冷 / lifesteal 吸血 / execute 斩杀 / shield 护盾 / regen 回血）作用于目标或自身；参数类（pierce 穿透 / multi 多重）改变命中方式。详见 `docs/design/sword_skills.md`。

剑气流云壁（元婴）与 斩鬼神 / 诛仙剑阵（化神）用到的三件事分别在三个地方生效：`cast_root` 在**施放瞬间**（`GameSession.Release`）、`knockback` 在**每次命中**（`GameSession.Hit`）、`targeting` 在**选敌**（`GameSession.Pick`/`Picks`，`sky_drop` 的阵心与其余形态的目标都走它）。`target` 类效果本来就按敌人 Id 结算、不做距离判断，所以「无视射程」不需要改动命中逻辑。

### 吸附（`gather`，12 寒冰龙卷）

`gather` 与 `knockback` 在 `GameSession.Hit` 里并列结算，写法刻意对称：

```
击退：e.X += sign(e.X − 玩家X) × knockback              # 背离玩家推开
吸附：e.X += sign(效果中心X − e.X) × min(gather, |效果中心X − e.X|)
```

- **"效果中心"就是 `CombatEffect.X`**：`ground` 的落点本来就是施放时选中那只的 X，所以"在敌人位置生成"不需要新形态。`target` / `sky_drop` 类的 X 同样是锁定的目标位置，将来别的技能要用也不必改代码。
- **取 `min` 是刻意的**：步长被夹在"剩余距离"上，敌人**收敛到中心就停**，不会被一口气拽过中心再来回弹。
- **速率 = `gather` ÷ 结算间隔**：`ground` 每 0.6 秒跳一次，所以 `gather = 60` ≈ **100 单位/秒**的吸力。它比青苔妖 155 的移速小，于是读作"聚拢并拖着走"，而不是"钉死在原地"；配上技能自带的 `chill`（减速 25%）正好压得住。
- 与击退同一套护栏：**死在这次伤害上的目标不再移动**（免得尸体滑动）、**裂隙永不移动**（它是关卡与停步判定的锚点）。
- **不占 `secondary`**：所以"吸 + 减速"能同时挂在寒冰龙卷上，不必二选一。
- **不要和 `knockback` 同配**：两者反向，同时挂着只会互相抵消（自检里有一条断言锁住这一点）。

### 各锁一敌（`band`，18 苍穹剑陨）

`sky_drop` 默认（`band = 0`）以**阵心**为中心按 `spread` 对称铺开，**与敌人数无关**（只有一个敌人时不缩成一束）。`band > 0` 换的是另一套：**每支各锁一个（尽量不同的）目标，落在它当时的位置爆炸**。

```
黑洞：以目标群的中轴为心，铺开 band 宽（两端夹进画面）
落点：第 i 支 → 它锁定的那只敌人的 X        （敌人不足时 Picks 循环重复）
```

- **锚在目标上，不锚在角色上**。这是这一版的关键：角色每秒走 340，一发 1.3 秒（探出 0.8 + 下落 0.5）的轰炸若从角色量起，落点会甩到身后几百单位——实测就是"人在走路，剑全落在空地上"。敌人每秒只走 100~260，且多数朝角色走，1.3 秒里的位移几十到两百，落点稳得住。
- **不再有"落在空地上"这回事**：落点与"打谁"从构造上就是同一个东西。
- **打 BOSS 不丢伤害**：敌人不足时 `Picks` 循环重复，所以场上只有一只时 `count` 支**全砸在它身上**（这也是 E1 口径 `N1 = projectile_count` 的来历）。
- **聚怪的收益直接翻倍**：寒冰龙卷把怪捏成一簇时，`count` 支落在同一片、爆炸圈叠起来。
- **目标中途死了也照炸那个位置**：与火海同一条契约（"击中谁是谁"，`sky_drop` 的 X 出手即冻结、不追踪）。
- **黑洞仍要夹在画面内**：逻辑画布恒为 1920、角色恒在 x=330（`BattleView.X` 的锚点），可用横向余量只有 1460，所以 `band` 上界 1400（加载期拦），运行期再把整条带子夹进 `[玩家X + 60, 玩家X + 1520]`。**落点不必夹**——它跟着敌人走，而敌人本来就在画面里。
- **表现**：`CombatEffect.SpawnX` 记出生点（**仅表现**，判定一律用 `X`）。剑在"出生点 → 落点"之间按下落进度插值，朝向取这条轨迹的角度——于是读作"自九霄收束而下"。`band = 0` 时 `SpawnX` 与 `X` 重合，行为与从前完全一致。
- **落地余韵**：`band > 0` 的天降在落地时，Core 会额外派生一个 **`Damage = 0` 的 `ground` 效果**（寿命 `GameSession.BurstLife`，0.3 秒）。天降效果本身在**落地当帧就被回收**（`RemoveAll(e => e.Life <= 0)`），不留这一下表现层就拍不到"炸开"、只能画坠落最后一帧。余韵是空伤害——`HurtEnemy(e, 0)` 什么都不改，而且寿命刻意短于 ground 的 0.6 秒结算间隔，**一次结算都走不到**。表现层按技能 ID（`skill_18`）把它画成放射状的剑气爆炸。

`chill`（寒冷）的移速效果与 `slow` 完全同源——命中时同时写 `SlowUntil` / `SlowFactor`，战斗判定只有一条路径；它额外写入 `ChillUntil`，仅用于让表现层把受击角色染成冰蓝。两者互相覆盖时以最后一次命中为准。技能预览与技能书对 `trigger_chance > 0` 的剑诀显示「普攻触发 x%」而不是冷却，避免把「最短触发间隔」读成「每 x 秒放一次」。

飞行形态（`trajectory`）决定弹道的运动方式，数量／间距／弧度／速度／出剑节奏都是正交旋钮，组合即可产出新技能而无需改代码：

- `bolt`（空）：直线飞向锁定目标，命中单体。剑灵弹丸等未指定形态的弹道走这条。
- `line_shot`：从角色肩侧**平射**、不追踪，命中路径上**最先遇到**的那个敌人即结算并销毁。配合 `pierce_chance` 可获得一次概率穿透：只在**第一次**命中时判定，穿透后同一支剑不再触发（`Pierced` 标记一次即永久关闭）。
- `line_pierce`：从角色身前**向前贯穿**，命中沿途每个敌人一次，越出关卡右界或寿命耗尽即消失（穿透由形态自带，不占用次级效果列）。剑气流云壁（贴地弧形剑气）与天剑（横空斩出的巨剑）都用它——形态只决定"怎么飞、怎么命中"，观感由技能 ID 分流。
- `sky_drop`：在最近敌人上空生成**固定剑阵**——各支按 `spread` 在阵心两侧铺开，**与敌人数无关**（只有一个敌人时不会缩成一束）；X 生成后**冻结**（不追踪，敌人走开就打空），停留 `hover_time` 秒、下落 `duration` 秒后，对落点 `aoe_radius` 内的所有敌人结算，因此可命中重叠单位。四个使用者（万剑决 / 焚天剑诀 / 苍穹剑陨 / 诛仙剑阵）的读法完全不同：从"钉住不动的密集剑雨"到"黑洞里一柄柄射出来"，观感全部由技能 ID 分流。
  - **天降火海**：`sky_drop` 且 `secondary = dot` 时，落地**不做一次性结算**，改为在落点留下一片火海——一个寿命为 `secondary_duration`（校验要求为正）的 `ground` 效果，半径沿用 `aoe_radius`。它每 0.6 秒对范围内全体结算一次并刷新灼烧，因此「落地后残留数秒的火海」与「持续掉血、重复命中只刷新不叠加」都由既有逻辑承担。焚天剑诀是当前唯一的使用者。
- `arc_homing`：追踪锁定目标，表现层按每支的 `arc` 画弧光（`arc` 在 `arc_min`～`arc_max` 间随机，由逻辑层一次摇定，不逐帧抖动）；区间带负值即为从下方掠过。
- `hover_homing`：先在施放者位置悬浮 `hover_time` 秒（表现层画在头顶剑阵位次上），再追踪锁定目标。**当前没有技能使用**，作为形态词汇保留。

多发时：追踪与平射形态**各自选敌、尽量不重复**（敌人不足才循环重复），`sky_drop` 则以阵心铺开落点；`volley_interval` + `volley_jitter` 决定出剑的先后节奏，`hover_time` 与它们一起折算成"起飞前停留"，因此命中时刻会随错时变化。没有合法目标时不空放、也不消耗冷却。

形态只影响"怎么飞、怎么命中"，伤害仍走统一的命中结算。当前**没有在役技能使用**的条目：次级效果 `pierce` / `multi` / `regen` 与 **`lifesteal`**（它原本由 14 万剑归心承载，该技能已改成 **剑罡护体** 用 `shield`，于是它也回到死配置），以及 **`vulnerable`**（它原本由 18 大庚剑阵承载，该技能已改成 **苍穹剑陨** 打一次性爆炸，于是它同样下线——将来由**剑意**授予某个技能分支）；形态 `hover_homing`；以及类别 `summon`（跟随召唤的剑侍已归档，机制留给以后的剑灵系统）。`stun` / `execute` / `shield` 分别由 07 御雷真诀 / 17 天剑 / 14 剑罡护体 在役承载，`mirror` 由 **19 剑二十三**承载。死配置的代码分支全部保留在 `GameSession` 里供退休配置日后复用，自检用内存改配置维持覆盖。两个增益类次级效果：

- `haste`：攻击速度提高，`secondary_value` 是**冷却流逝加快**的比例（当前 `skill_04` 填 `0.25` = 流逝 ×1.25，等效冷却缩短 20%；早期曾填 `3` = ×4），`secondary_duration` 是持续秒数。**只加速输出类剑诀与剑灵的冷却，不加速增益类剑诀**——否则 15s 冷却 / 6s 持续的增益会在持续期内转好，变成 100% 常驻。文案统一写「冷却流逝 +X%」，别和「冷却缩短」混用，两个口径差 4 倍。
- `crit_reduce`：暴击率**绝对**提高（0.3 = +30 个百分点），`secondary_extra` 是每次暴击缩短的冷却秒数。缩短目标只从"已习得、当前冷却 > 0、且**不是增益类**"的技能里随机抽取，没有候选就什么都不做；并且**每次施法最多触发一次**（由该次施法的第一支弹丸负责）——暴击是逐弹丸摇的，多发齐射一轮 3～5 支，若每支都触发，触发密度会被放大数倍，增益的冷却也会被不断吃掉。

另外：配置里 `power = 1` 的增益剑诀**不占用伤害倍率窗口**（`_buffPower`/`_buffTime`），这样纯功能向的增益不会把正在生效的伤害重置掉。判断用的是配置的基础 `power`，不是算上等级与剑意之后的倍率。

### 影分身（`secondary = mirror`，19 剑二十三）

生效期间，本体**每释放一个剑诀**，分身就在身后同步再放一份，伤害按继承比例打折。

- 继承比例 = `secondary_value + 0.01 × (技能等级 - 1) + 剑意加成`，**不封顶**（用户明确要的后期成长轴）。因此剑二十三那 4 行 `SwordUpgrade` 的 `value` 填 `0.01`（其他技能是 `0.08`）——两条轴都是「每级 +1%」，否则一条剑意等于 +8 个百分点，满配会算出 **759%** 这种数。
- 镜像走的是 `LaunchShape` 而**不是** `Release`。这是关键：冷却由 `Release` 的调用方写，镜像这一份**不写冷却**，于是不会多发一次释放音（释放音只认「冷却 0 → 正」的边沿，见 `UI/Audio.cs`），也不会把 `cast_root` 的全屏定身重复结算一次。
- 跳过增益类（对分身没有意义）与召唤类（召唤物会活过分身寿命），并跳过影分身自身，避免无限递归。
- `CombatEffect.Mirrored` 标记分身那一份：表现层据此不再挂第二次技能名标签，`Launch` 里的暴击缩冷却也不认它——分身出手不该替本体缩冷却。
- 弹道类（`bolt` / `line_shot` / `line_pierce` / `arc_homing` / `hover_homing`）从**分身**的位置出发（`MirrorOffset = -110`，本体身后，玩家朝右推进所以是更小的 X）。**起飞前停留期间也跟分身走**：`TickEffects` 里让 `line_shot` / `line_pierce` 跟着施法者走的那一行必须带上这个偏移，否则镜像会被拽回本体身上、两支剑完全重叠（御剑术前摇 0.12 秒、剑气流云壁 0.5 秒，都够把偏移抹掉）。
- **分身那一式晚 0.18 秒（`GameSession.MirrorDelay`）出现**。`CombatEffect.Delay` 在 `TickEffects` 的循环开头拦一道、在 `Life` / `Timer` 递减之前，所以延迟期间寿命与倒计时都不流逝；到点后按原有逻辑照常走。表现层在 `DrawEffect` 里跳过 `Delay > 0` 的效果（否则会先看到一支僵在原地的剑）。这一拍是必须的：`target` / `ground` / `sky_drop` 三类的起点本来就是目标的 X、根本没有发射点，没有延迟就一点视觉痕迹都没有——受影响的在役技能有 万剑决 / 焚天剑诀 / 苍穹剑陨 / 诛仙剑阵 / 天剑 / 寒冰龙卷 / 御雷真诀 / 斩鬼神 共 8 个。有分身那一式待发时，表现层会在分身处叠一圈剑气光，把那 7 个的因果关系也点明。
- 已知取舍：镜像的 `dot` 火海会把本体的灼烧刷新成较弱的那份（`ApplySecondary` 是赋值而非叠加）；`knockback` 会翻倍（剑气流云壁 160 → 320）。都属可接受，平衡轮再定。

### 利用状态（`secondary = bonus_vs_state`，10 斩鬼神）

对**携带任意状态**的目标增伤：命中时若目标身上有 slow / chill / stun / dot / vulnerable 任一（`EnemyState` 对应的 `*Until > 0`），本次伤害 × `(1 + secondary_value)`。

- 它**不写任何状态字段**，只在 `Hit` 里当一个乘区——`ApplySecondary` 的 switch 没有这一支，天然 no-op，所以它不占用"上状态"的能力。
- 判定必须放在 `ApplySecondary` **之前**（那一句在 `Hit` 的末尾），否则会把本次刚要挂上的状态也算成「已有的」。
- 判定集合与 `ApplySecondary` 对齐。注意 `StunUntil` 也被 `cast_root`（剑气流云壁的全屏定身）写入，所以**被定身的目标也算「有状态」**——这是有意的。
- 表现层在目标带状态时多画一圈「破绽」环；判断直接读目标的状态字段，Core 不为它新增标记。
- **为什么选斩鬼神当载体**：它锁的是场上最厚的那只（通常是 BOSS / 精英），而长战斗中恰好是它身上会叠满状态——收益落在最需要的地方。

### 层与定位

怪物分**地面 / 空中**两层（`monster.layer`，空 = 地面），技能声明自己能打到哪一层（`SwordSkill.hits`，空 = 都能打）。两边必须配对才结算：

| `hits` | 打地面 | 打空中 |
| --- | --- | --- |
| 空 / `both` | ✓ | ✓ |
| `ground` | ✓ | ✗ |
| `air` | ✗ | ✓ |

三处收口，缺一不可：

1. **选敌**：`Target` / `Targets` / `TargetsWithRepeats` / `Pick` / `Picks` 都带 `hits`，剑诀传 `skill.Hits`，**普攻、剑灵弹与召唤弹一律传空**。地面招因此不会去锁飞行单位，也就不会"空放还吃冷却"。
2. **命中结算**（`GameSession.Hit`）：`LayerHit` 不过直接返回——这是最后一道防线，`aoe_all` 那种全场扫描也逃不掉。
3. **范围扫描**：`TickEffects` 里按半径/扫过区间取敌的那几处（`line_pierce` 推进、`sky_drop` 落点与全体、`ground` 每 0.6 秒、锁定型的取目标）都换成 `Legal(e, effect.Layer)`，否则会出现"看着没碰却掉血 / 碰到了却不掉血"。

**层不写进 `EnemyState`**，而是从 `MonsterId` 查模板推导：存档格式不用改，老存档里的 `MonsterId` 照样能定位。天降火海派生出的 `ground` 子效果会**照抄父效果的 `hits`**，所以焚天剑诀定为 `ground` 时，它的火海也打不到空中。

当前 15 个在役剑诀里 `hits` 定位为 `ground` 的只剩 02 焚天剑诀 / 05 剑气流云壁，其余留空（`both`）；**`air` 目前没有使用者**，作为词汇保留（和 `hover_homing` / `pierce` 一样，用改过配置的自检维持覆盖）。注意别把 `hits` 与 `kind` 弄混：12 寒冰龙卷的 `kind = ground`（它是落在地上的力场），但 `hits` 留空，照样打得到空中的敌人。

### 触发概率（真诀）

`trigger_chance > 0` 的剑诀走另一条出手路径：

- **不参与**「冷却到点自动释放」。冷却照常流逝，但只有普攻出手时才会摇概率。
- 每次**普通攻击**真正出手的那一刻（见 `fightattr.csv`），对每个「已习得、当前冷却就绪、`trigger_chance > 0`」的剑诀各摇一次；命中则释放，并把冷却重置为完整值。冷却未就绪的不摇——省下一次随机数，也让「冷却就绪才可能触发」这句话成立。
- **概率可以逐次累加**（`trigger_chance_step > 0`）：没摇中就加上一步，摇中后清零。它把"看脸"换成"越打越近"：既不会冷却一好就放，也不会连十几次摇不中。**当前没有任何在役剑诀用它**（`step` 全为 0，这一路退化成固定概率）——寒冰龙卷曾经用过（15% 起、每次普攻 +5%），后来金丹整层去概率化时改回了固定冷却，机制保留在代码里供剑意扩展复用。
- 因此配置里的 `cooldown` 对真诀是**最短触发间隔**，实际间隔由触发概率主导：普通攻击每秒一次，概率 p 对应平均 1/p 秒一次。
  **全名单只剩一个真诀**：07 御雷真诀（`trigger_chance` 10%，冷却 5s → 期望周期 ≈ 5 + 10 = **15 秒**）。金丹三式曾经都是概率触发（都填 0.3 的临时调试值），实测下来"整个境界不可控、成长反馈不明显"，于是 02 焚天剑诀与 12 寒冰龙卷改回固定冷却——玩家花 1800 灵钱解锁一整个境界，拿到的该有"我说了算"的手段。
- 真诀**不参与**「暴击缩短一个随机技能的冷却」的候选池：它们的冷却只是最短间隔，缩几秒几乎等于白给（与排除增益类剑诀同一口径）。
- 技能预览模式下普攻是关掉的，真诀改为按预览节奏走 `GameSession.ForceRelease` 显式释放，否则在对照台里永远看不到它们出手。

## Talent.csv

| 字段 | 类型 | 含义 |
| --- | --- | --- |
| id | string | 稳定唯一 ID |
| name | string | 中文显示名称 |
| column | integer | 天赋横向列号，从1开始 |
| row | integer | 天赋纵向行号，1～5 |
| max_level | integer | 最大等级 |
| cost_gold | number | 灵钱消耗；天赋按当前等级+1乘此值 |
| cost_core | integer | 每次天赋升级消耗妖核 |
| effect | enum | 天赋 `atk` / `hp` / `auto_intent`。加载期校验取值，未知值直接拒绝——`TalentBonus` 按它分类查询，填错会让这个节点静默失效 |
| value | number | 效果数值，或全局设置的值 |

## TalentLink.csv

| 字段 | 类型 | 含义 |
| --- | --- | --- |
| id | string | 稳定唯一 ID |
| from_id | reference | Talent.id，前置节点 |
| to_id | reference | Talent.id，后继节点 |

## Equip.csv

| 字段 | 类型 | 含义 |
| --- | --- | --- |
| id | string | 稳定唯一 ID |
| name | string | 中文显示名称 |
| base_atk | number | 武器基础攻击 |
| craft_cost | number | 打造并替换装备消耗灵钱 |
| upgrade_cost | number | 武器每次淬炼成本基数 |
| refine_cost | number | 洗练灵钱成本 |

## SwordUpgrade.csv

| 字段 | 类型 | 含义 |
| --- | --- | --- |
| id | string | 稳定唯一 ID |
| name | string | 中文显示名称 |
| tier | integer | 剑意页0～3 |
| skill_id | reference | 剑意引用SwordSkill.id，剑灵引用PetSkill.id |
| currency_id | reference | 消耗item.id |
| max_level | integer | 最大等级 |
| cost | number | 基础升级成本；具体成长规则见下方 |
| value | number | 效果数值，或全局设置的值 |
| effect | enum | 剑意取 `damage_percent`（技能威力）/ `inherit_percent`（影分身的继承比例，只有剑二十三那 4 行用）。**加载期校验取值**，未知值直接拒绝——`SkillBonus` 按它分类查询，填错会让这行静默失效。扩展词汇表见 [sword_intent.md](../design/sword_intent.md)（**白名单只放已经有消费点的取值**，不做"先放开、实现留到以后"） |

## Pet.csv

| 字段 | 类型 | 含义 |
| --- | --- | --- |
| id | string | 稳定唯一 ID |
| name | string | 中文显示名称 |
| skill_id | reference | 剑意引用SwordSkill.id，剑灵引用PetSkill.id |
| weight | number | 抽取权重 |
| visual | string | Assets/visuals.json 中的表现 ID |

## PetSkill.csv

| 字段 | 类型 | 含义 |
| --- | --- | --- |
| id | string | 稳定唯一 ID |
| name | string | 中文显示名称 |
| cooldown | number | 独立冷却秒数 |
| power | number | 伤害/效果倍率 |
| range | number | 目标选择距离（逻辑坐标） |
| kind | enum | 类别；对应表的已注册行为/分类 |

## PetEquip.csv

| 字段 | 类型 | 含义 |
| --- | --- | --- |
| id | string | 稳定唯一 ID |
| name | string | 中文显示名称 |
| category | string | 剑灵增强类别，同一剑灵不可重复 |
| power | number | 伤害/效果倍率 |
| cost_gold | number | 灵钱消耗；天赋按当前等级+1乘此值 |

## wave.csv

| 字段 | 类型 | 含义 |
| --- | --- | --- |
| id | string | 稳定唯一 ID |
| interval | number | 刷怪间隔秒数 |
| elite_every | integer | 每多少波额外刷一个精英 |
| elite_id | reference | 精英monster.id |
| hp_scale | number | 本波**普通怪**的 HP 系数，乘在 `level.normal_hp` 之上（大于 0，上界 5） |
| atk_scale | number | 本波**普通怪**的 ATK 系数，乘在 `level.normal_atk` 之上（大于 0，上界 5） |
| count | integer | **第 1 关这一波刷几只**（大于 0）。这是**整波**的只数，不是某一个模板的 |
| count_max | integer | 只数的**封顶**（不小于 `count`）。到了就不再涨 |

**一条波次刷什么怪由子表 `wave_unit.csv` 决定**（见下），因为一条波次要混编多种怪，一行装不下。

`hp_scale` / `atk_scale` 是**波次自身**的强弱梯度，用来做"同一关里这一波比那一波硬"；**只作用于 `kind = normal`**，精英 / BOSS / 裂隙只吃 `level.csv` 的关卡倍率——它们是关卡节点，不是波次阵容的一部分。上界 5 是防手滑（想写 1.2 写成 12），量级调整应该去改 `level.csv` 的关卡倍率，不要在波次系数上做跳变。

**只数为什么放在波次这一层**：各模板只负责"刷什么、按什么比例"，难度只由 `count` / `count_growth` / `count_max` 决定。两者分开之后，**改阵容不会顺手改掉这波的只数**，改只数也不会把"两只小怪配一只肉盾"摊成一样多。旧写法是各模板各自乘一个关卡倍率，`count = 1` 的模板会**跳变**（第 2 关 `round(1.33)` 还是 1、第 3 关直接跳到 2），给不出稳定的"每关 +1"。

## wave_unit.csv

| 字段 | 类型 | 含义 |
| --- | --- | --- |
| id | string | 稳定唯一 ID |
| wave_id | reference | wave.id |
| monster_id | reference | monster.id |
| count | integer | 该模板在这一波里的**比例权重**（大于 0） |

刷怪点按**跨模板的连续序号**铺开（第 i 只落在 `格内偏移 40 + i × 100` 处），所以混编时两种怪不会叠在同一点；精英落在所有普通单位之后。

**只数按关卡算，再按权重摊给模板**（`GameSession.WaveCountsFor`）：

```
关卡倍率 = 1 + (order−1) × wave_growth + (order−1)² × wave_accel      # 全局设置，见下
整波只数 = min(count_max, round(count × 关卡倍率))
各模板只数 = 整波只数 × 自己的权重 ÷ 权重之和                          # 先向下取整，余数按小数部分从大到小补一只
```

- **摊出来的只数合计恰好等于整波只数**：先按比例向下取整，再把缺的那几只按"被舍掉的小数部分"从大到小补上；并列时按表内顺序，所以结果确定可复现（自检直接断言过这条和"每个模板都刷得出怪"）。
- 权重是**比例**，不是只数：写 `2 : 1` 意味着"永远是三只里两只小怪配一只肉盾"，与这一波刷 3 只还是 8 只无关。
- 封顶 `count_max` 是硬约束：`count_growth` 再大也不会超过它（自检里把 `count_growth` 调成 9 验过）。

当前九条波次的配置（都是 `count 3`，封顶各自不同）：

| 波次 | 阵容偏向 | 权重 | 满员（封顶）时 |
| --- | --- | --- | --- |
| wave_1 | 近战潮（基础） | slime 2 : tank 1 | 13 只：青苔妖 9 + 石甲兽 4 |
| wave_2 | 疾袭（高速突脸） | swift 2 : slime 1 | 22 只：疾影妖 15 + 青苔妖 7 |
| wave_3 | 空袭（空中为主） | bat 1 : hawk 1 : slime 1 | 20 只：夜枭 7 + 飞蝠 7 + 青苔妖 6 |
| wave_4/5/6 | 后 50 关那一档（更硬） | 见配置 | 上限 14 / 18 / 19 |
| wave_7 | 远程阵 | archer 1 : mage 1 : slime 1 | 20 只 |
| wave_8 | 混编（地空高速） | slime 1 : swift 1 : bat 1 | 20 只 |
| wave_9 | 重甲阵 | tank 2 : archer 1 | 13 只 |

关卡与波次的对应在 `level.csv` 的 `wave_id`：**前 50 关六循环**（`wave_1 / 2 / 3 / 7 / 8 / 9`，于是第 1~6 关各一套不同阵容），后 50 关仍是三循环（`wave_4 / 5 / 6`）。

**护栏**（都在加载期拦）：每条 wave 至少要有一条 wave_unit（否则那一波刷不出怪、关卡直接空转）；`count_max` 不得低于 `count`（否则关卡越高刷怪越少）；`weight` 必须为正（0 就是一行什么都不刷的死配置）；一条 wave 的**只数上限**（含每 `elite_every` 波跟着的那只精英）不得把最末一只刷到第 3 格之外——落点是 `40 + i × 100` 连续铺开的（与 `GameSession.Spawn` 同一份约定），上限抬高不是免费的。

### 伤害统计（GM 面板 →「伤害统计」）

按技能列出 **有效伤害 / 占比 / 溢出 / 每秒 / 命中**，用来在游戏里核对数值平衡——buff 与纯功能类技能不造成伤害，因此不出现。口径：

- **有效 = 实际打掉的血**。一次打掉 500 但目标只剩 120 血，有效只记 120——这才是"贡献"。
- **溢出 = 名义伤害里超出剩余血量的那部分**（上例是 380）。单列一栏是为了看出"哪个技能经常砸在尸体上"。
- **每秒 = 累计 ÷ 统计时长**（一个**统一分母**，所以各技能可以直接横比）。面板上显示统计时长并配重置按钮，**换关不清零**。
- **来源**：剑诀 / 宠物技能 id；**空串 = 普通攻击**（单列一行，是最重要的对照项）。灼烧的跳伤归到**施加它的那个技能**（`EnemyState.DotSkill`）——多个来源的灼烧只留最后一次施加者，与 `DotDps` 同一条"赋值而非叠加"的口径。
- **影分身那一份归到剑二十三**（`CombatEffect.DamageSource`）：它按继承比例复制出来的伤害算**剑二十三自己的价值**，而不是藏在被复制的那一式里——否则"剑二十三到底值多少"永远查不出来。
- **已习得的剑诀全部在列**（含 0 伤害的功能类，如仙风云体术 / 醉仙望月步），0 就是"还没接进统计"；**没习得的不会出现**，所以"某个技能没在表里"和"在表里但是 0"是两件不同的事——前者是没学，后者是有待接入。

> ⚠️ **`命中` 这一列对持续伤害不可比**：灼烧的跳伤是**每步（0.05 秒）结算一次**、每次记一次命中，所以带 dot 的技能（焚天剑诀）命中数会是天文数字。**伤害数字是对的**，只有这一列要当心。

数据源是 `_battle.Session`（**当前正在看的那一场**），所以技能预览模式下选中某个技能，就能当场读出它自己的每秒伤害。

### 调试用的 GM 开关（不写配置、不落盘）

| 开关 | 作用 | 边界 |
| --- | --- | --- |
| 波次数量 | 每波额外多刷几只，种类从普通怪原型里**随机挑**（顺带把地/空、快/慢、远/近的比例搅乱） | 0～20（落点是 `40 + i×100` 铺开的，堆太多会溢到下一格）；**只影响之后刷出的波次** |
| 怪物血量 / 攻击倍率 | 乘在所有关卡与波次倍率**之后**，精英 / BOSS / 裂隙同样吃 | 0.1～100，走 `.5/1/2/3/5/8/12/20/50` 的台阶；**改完立刻按比例缩放场上已有的怪**（保持血量百分比），不必等下一波 |
| 主角无敌 | `HurtPlayer` 直接返回：**不掉血、不死亡** | 为什么需要它：死亡会清增益并**重置冷却**，于是伤害统计里各技能的周期会断掉、每秒数字失真——挂一局量数字前先打开它 |

四个都在 `GameSession` 上（`WaveBonus` / `MonsterHpScale` / `MonsterAtkScale` / `PlayerInvincible`），只读写在会话态上——`wave.csv` / `monster.csv` 保持正式数值。

> 顺带：**无敌会改变随机数的消耗**（`HurtPlayer` 里那次闪避判定被跳过），所以开关前后同 seed 的随机序列不再一致。调试用，不影响正式流程。

测"怪物多的时候技能够不够爽"**不动正式配置**：`GameSession.WaveBonus`（GM 面板「波次数量」的 ＋ / −）让每波额外刷几只，**种类从普通怪原型里随机挑**——所以它顺带把地/空、快/慢、远/近的比例搅乱，正好用来观察各技能的覆盖。它是**会话态**（不落盘、不写 `wave.csv`），只影响之后刷出的波次。上限 20 是防手滑：落点是 `40 + i×100` 铺开的，堆太多会溢到下一格（画面难看，不影响判定）。

## drop.csv

| 字段 | 类型 | 含义 |
| --- | --- | --- |
| id | string | 稳定唯一 ID |
| group_id | string | 奖励组ID；同组多条一起发放 |
| item_id | reference | item.id |
| amount | number | 必得数量；首杀妖核必须恰好1 |

## spawn_point.csv

| 字段 | 类型 | 含义 |
| --- | --- | --- |
| id | string | 稳定唯一 ID |
| offset | number | 每格内刷怪点横坐标 |

## contemplation.csv

| 字段 | 类型 | 含义 |
| --- | --- | --- |
| id | string | 稳定唯一 ID |
| name | string | 中文显示名称 |
| item_id | reference | item.id |
| capacity | number | 每类未收取产物上限 |
| click_amount | number | 单次点击或自动参悟产量 |
| auto_interval | number | 自动参悟秒数 |

## game_settings.csv

| 字段 | 类型 | 含义 |
| --- | --- | --- |
| id | string | 稳定唯一 ID |
| value | number | 效果数值，或全局设置的值 |

## 当前公式与全局设置

- 剑诀消耗：`ceil(cost × cost_growth ^ 当前等级)`；当前等级0表示尚未习得。
- 天赋消耗：`cost_gold × (当前等级+1)` 灵钱，加 `cost_core` 妖核；所有货币充足才一起扣除。
- 剑意消耗：`cost × (当前等级+1)` 份对应剑意。
- 武器攻击：`base_atk × (1 + 0.15 × 淬炼等级) × 洗练品质`。
- 最终攻击：`(基础攻击 + 武器攻击) × (1 + 基础攻击加成 + 天赋攻击加成)`。
- 技能伤害：`最终攻击 × power × (1 + skill_level_bonus × (技能等级-1) + 剑意加成)`，再应用 Buff 及暴击。
  - `skill_level_bonus`（默认 **0.15**）是**技能等级**每级的威力加成。它从代码里提出来放进本表，是为了让技能页「每级 +X%」的提示与实现**同源**——硬编码会让提示和实际悄悄对不上。
- **剑罡护体（`secondary = shield`）的环绕飞剑**：护盾生效期间，每 `guard_interval` 秒向 `guard_range` 内最近的合法敌人射出一柄小剑，威力 = `最终攻击 × guard_blade_power`。三个参数都在 `game_settings.csv`。
  - **出手范围必须与剑诀自己的 `range` 分开**：`range` 管的是"能不能施放这个增益"（要够得着敌人才放）。两者曾被我压成同一个值（射程 400 当近身范围），结果是**远程怪在场时增益根本放不出来**，预览页更惨——靶子固定在 520，射程压到 400 之后剑罡护体**连护盾都上不了**，画面上什么都没有。
  - 射出的小剑 `index` 传 1，不占"技能名标签"与"暴击缩冷却"的名额。护盾不在时什么都不做。
  - 自检有一条护栏：**每个剑诀的 `range` 都不短于预览靶距（520）**——射程比靶距还短的剑诀在预览页永远演示不出任何东西。
- **增益类剑诀的升级**：峰值强度（`secondary_value`）**不随等级变**，成长全在**覆盖率**上——实际冷却 = `max(持续 × buff_cooldown_floor_ratio, 冷却 × (1 - buff_cooldown_per_level × (技能等级-1)))`。下限把覆盖率封在 **80%**（`floor_ratio` 1.25 ⇔ 1/1.25），避免变成常驻——与「攻速不加速增益类剑诀」是同一条护栏。`buff_cooldown_per_level` 默认 0.02。
  - 起因：`仙风云体术` / `醉仙望月步` 的 `power = 1`，因此 `CastBuff` 不写共享倍率窗，而它们真正的强度走 `secondary_value`（定值）——**升级原本完全没有收益**，玩家花灵钱点「强化」什么都没发生。
  - `万剑归心`（`power 1.5`，走倍率窗）与 `剑二十三`（继承比例随等级涨）原本就有强度成长，**再加上覆盖率成长会双重收益**——`剑二十三` 因此在后段偏强，已在 [balance_ttk.md](../design/balance_ttk.md) 第 9 节记为待复核项。
- **目标中途死亡的补救**：追踪弹（`hover_homing` / `arc_homing`）与定点弹（`target`）在**发射时**锁定目标，而目标可能在弹丸飞到之前就被别的技能打死。原先这种情况**一次伤害都不结算**——青元剑芒专挑残血，于是系统性白飞；御雷真诀 / 斩鬼神的 15~30 秒大招也会白白打空。现在分两条路：

  | 形态 | 目标死亡后 |
  | --- | --- |
  | **追踪弹** | 先**沿原轨道飞完这一程**（到它最后所在的位置），再做**落点重索敌**：在 `FallbackRadius`（80 单位）内挑**血量最低**的合法敌人接着追，并把**弧度取反**——画面上是一道反向的弧，两段连起来像个波。最多改索 `MaxReacquire`（2）次。**索不到敌就消散，本次伤害丢失**（有意如此，不做兜底命中）。 |
  | **定点弹** | 它没有飞行过程、没有表现可保，所以直接在落点 80 单位内挑血量最低的**当场结算**一次。 |

  - 两条都只在落点附近找、**不改追远处**：多发齐射若都能改追远处，会收敛到同一只（`Picks` 刻意"尽量不重复"就是为了铺开）。
  - 表现层配套修了一处：`FlightProgress` 原先在目标消失时回退成 `x + 200`，把进度钉死在弧顶附近，剑芒因此**一直飘在半空、再凭空消失**；现在改为飞向 `TargetX`（它最后所在的位置）。
- 普通攻击伤害：`最终攻击 × fightattr.basic_power`，每 `fightattr.basic_interval` 秒对最近的合法目标平射一柄飞剑（见 `fightattr.csv` 一节）。它照常吃暴击，但**不触发**「暴击缩短一个随机技能的冷却」。
- `starting_gold`：初始灵钱120；`cell_width`：1920；`respawn_seconds`：复活等待秒数。
- 接近裂隙的停步判定取**已习得剑诀的最大射程**（`GameSession.AttackRange`），**不并入普攻射程**：并进去会让角色停在裂隙射程外空转。普攻射程单列在 `fightattr.basic_range`，它不小于停步距离，因此角色站定时必定够得着。
- `pet_draw_cost`、`pet_duplicate_gold`：召唤成本与重复返还。
- `fixed_step`：模拟固定步长；`save_interval`：自动存档间隔；`enemy_visual_limit`：仅绘制数量上限，不限制怪物存在数量。
- `wave_growth` / `wave_accel`：波次只数随关卡放大的线性项与平方项 —— `关卡倍率 = 1 + (order-1) × wave_growth + (order-1)² × wave_accel`（见 `wave_unit.csv` 一节）。两项都为正，所以"数量在涨"和"涨得越来越快"是两件可以分别调的事。**逐波的基线与封顶在 `wave.csv`**（`count` / `count_max`），倍率仍是全局的。

## 修改与导出

使用UTF-8保存CSV，避免Excel将ID自动转换为数字或科学计数法。表头不可随意更名。修改后运行根目录README中的验证命令，再重启游戏。导出工具以标准CSV引号规则写出并重新读取验证，不覆盖玩家存档。


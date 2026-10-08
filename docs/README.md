# Idle-Sword 项目文档

本目录是项目设计与技术文档的统一入口。Godot 工程位于 `../idle-sword/`。

## 文档索引

- [版本计划（Ver1.0 → Ver3.0）](planning/roadmap.md) —— **要知道"接下来做什么"看这份**
- [文档一致性排查（待改清单，带行号）](design/doc_audit.md) —— **动文档前先看这份**
- [设计镜片：养成系统的三层流转](design/design_lens.md)
- [战斗价值清单（可投放的价值门类）](design/value_inventory.md)
- [已确认规则与待确认边界](design/core_rules.md)
- [战斗底层规则（DMG1 / DMG2 / DMG3）](design/combat.md) —— **战斗公式与结算顺序的权威口径**
- [战斗属性与 DamageModifier 规范](design/fightattr.md) —— **要加战斗属性前先看这份**
- [世界观与包装](design/worldview.md)
- [怪物设计与「层 / 定位」轴](design/monsters.md)
- [剑意（参悟）的包装与分页](design/intent_packaging.md)
- [数值刻度与 TTK 规划（草案，待确认）](design/balance_ttk.md) —— **做数值计算前必读**
- [十五法术设计](design/sword_skills.md)
- [法术境界编排](design/skill_realms.md)
- [十五法术设计评审（诊断 + 方向性建议）](design/skill_review.md) —— **进入参悟等扩展前先看这份**
- [十五法术数值存档](design/skill_values.md) —— **调技能数值前必读**（口径、当前值、目标值、调优旋钮）
- [参悟系统设计（草案，待讨论）](design/sword_intent.md)
- [技能结构方案（三层拆表：Effect / Buff / Timeline / Trigger）](battle/技能结构方案.md)
- [在役技能逻辑清单（技能名 / 描述 / 实际效果 / 可触发的逻辑）](battle/技能逻辑清单.md) —— **要改某个技能时先看这份现状快照**
- [核心成长系统认知文档（修行 / 法术 / 神通 / 神识）](battle/核心成长系统认知文档.md) —— **四系统"职责与边界"的唯一权威口径；写代码或配置前遇到归属问题看这份**
- [核心成长系统讨论稿（架构框架）](battle/修行&法术&神通&神识系统框架.md) / [神通系统修正](battle/神通系统修正.md) —— 推导过程与被否决的方案，结论已固化进上面那份认知文档
- [战斗评估系统方案（战斗探针）](battle/DPS评估与战斗模拟系统方案.md) —— **SU / TTK 为纲；与工程的对账差异在文末**。内核 `Features/Battle/CombatProbe.cs`，出口是关卡编辑器的只读节「战斗探针」
- [工程框架方案](technical/architecture.md)
- [配置表规划](data/config_plan.md)
- [CSV 字段字典](data/fields.md)
- [修行星图节点清单（ID → 功能）](data/talent_nodes.md)
- [第一阶段实现与暂定规则](planning/phase_01.md)
- [美术资源规范](art/asset_spec.md)
- [参考资料](reference/zad_archery.md)
- [启动与验证](../README.md)

## 维护约定

- 文档使用 UTF-8 Markdown；代码、配置、美术与文档分别存放。
- 区分“已确认规则”“实现建议”“待确认”，不得把建议写成既定需求。
- 设计变更同步更新相关文档，过时规则直接修订，保留必要的变更说明。
- 字段名称、单位、引用关系和公式统一维护在 `data/`；不要只写在聊天记录或代码中。
- 当前阶段已完成可运行框架原型；区分已实现功能与正式内容设计，详见阶段交付说明。

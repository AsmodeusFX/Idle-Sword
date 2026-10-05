# Idle-Sword 项目文档

本目录是项目设计与技术文档的统一入口。Godot 工程位于 `../idle-sword/`。

## 文档索引

- [文档一致性排查（待改清单，带行号）](design/doc_audit.md) —— **动文档前先看这份**
- [已确认规则与待确认边界](design/core_rules.md)
- [世界观与包装](design/worldview.md)
- [怪物设计与「层 / 定位」轴](design/monsters.md)
- [剑意（参悟）的包装与分页](design/intent_packaging.md)
- [数值刻度与 TTK 规划（草案，待确认）](design/balance_ttk.md) —— **做数值计算前必读**
- [十五法术设计](design/sword_skills.md)
- [法术境界编排](design/skill_realms.md)
- [十五法术设计评审（诊断 + 方向性建议）](design/skill_review.md) —— **进入参悟等扩展前先看这份**
- [十五法术数值存档](design/skill_values.md) —— **调技能数值前必读**（口径、当前值、目标值、调优旋钮）
- [参悟系统设计（草案，待讨论）](design/sword_intent.md)
- [工程框架方案](technical/architecture.md)
- [配置表规划](data/config_plan.md)
- [CSV 字段字典](data/fields.md)
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

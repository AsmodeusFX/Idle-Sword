using Godot;
using IdleSword.Core;

namespace IdleSword.UI;

public partial class Main
{
    /// <summary>
    /// 技能悬停提示的全文。**玩家能拿到的全部口径就在这里**，所以三块都要有：
    /// ① 名字 + 等级；② 描述；③ **关键参数**（当前值 → 下一级）；④ 升级到底强化了什么。
    ///
    /// `rate` / `nextRate` 只算**威力倍率**（含技能等级与参悟），实际伤害由 `SkillText.Params` 乘上
    /// 当前面板攻击力——这样提示里显示的是"按你现在这个练度能打多少"，而不是一个抽象倍率。
    ///
    /// ⚠️ **没习得（`rank = 0`）时按"习得后（1 级）"显示**，不能按 0 级算：`1 + 0.15×(0−1)` = 0.85，
    /// 那会给出一个**负成长**的读数（"当前 Lv.0 → 威力 ×0.85"），比不显示还糟。
    /// </summary>
    private string SkillTip(SkillDef skill)
    {
        int rank = _game.State.Skills.GetValueOrDefault(skill.Id);
        int shown = Math.Max(1, rank);
        string level = rank > 0 ? $"等级 {rank}/{skill.MaxLevel}" : "尚未习得";
        double rate = SkillRateAt(skill, shown), next = SkillRateAt(skill, shown + 1);
        // 环绕飞剑的参数（剑罡护体用）住在 `game_settings` 里，不在 SwordSkill 行上，所以显式传进去。
        return $"{skill.Name}　{level}\n\n"
            + $"{skill.Description}\n\n"
            + SkillText.ParamsBlock(skill, _game.Attack, rate, next,
                _game.Config.Setting("guard_blade_power"), _game.Config.Setting("guard_interval")) + "\n\n"
            + UpgradeTip(skill, shown);
    }

    /// <summary>某一级下的 **SkillRate**（`power × (1 + 技能等级成长 + 参悟成长)`）。
    /// 与 `GameSession.SkillPower` 是同一条口径——提示里的"下一级能打多少"必须与实现同源，否则提示会撒谎。</summary>
    private double SkillRateAt(SkillDef skill, int rank) =>
        skill.Power * (1 + _game.Config.Setting("skill_level_bonus") * (rank - 1) + _game.SkillBonus(skill.Id, "damage_percent"));

    /// <summary>
    /// 升级到底强化了什么——技能页「强化」按钮的悬停提示用它，两类分开写：
    /// - **输出类**：每级威力 +`skill_level_bonus`（就是 `SkillPower` 用的那个系数，两边同源）。
    /// - **增益类**：峰值强度**不随等级变**（它走 `secondary_value`，是定值），成长全在**覆盖率**上——
    ///   每级冷却 −`buff_cooldown_per_level`，下限 `持续 × buff_cooldown_floor_ratio`（覆盖率封顶 80%）。
    ///   剑二十三另有一条"继承比例 +1%/级"（它的强度确实随等级涨）。
    /// 所有数字都从 `game_settings` 读；硬编码会让提示与实现悄悄对不上。
    /// </summary>
    private string UpgradeTip(SkillDef skill, int rank)
    {
        var cfg = _game.Config;
        if (skill.Kind == "buff")
        {
            double per = cfg.Setting("buff_cooldown_per_level"), ratio = cfg.Setting("buff_cooldown_floor_ratio");
            double cd = Math.Max(skill.Duration * ratio, skill.Cooldown * (1 - per * (rank - 1)));
            string extra = skill.Secondary == "mirror" ? "；另有：继承比例 +1%/级" : "";
            return $"增益类：峰值强度不随等级变，成长在覆盖率上。\n每级冷却 −{per:P0}（下限 持续×{ratio:0.##} = 覆盖率上限 80%）"
                + $"　当前 Lv.{rank} → 冷却 {cd:0.#}s，覆盖率 {skill.Duration / cd:P0}{extra}";
        }
        double mul = 1 + cfg.Setting("skill_level_bonus") * (rank - 1) + _game.SkillBonus(skill.Id, "damage_percent");
        return $"每级威力 +{cfg.Setting("skill_level_bonus"):P0}　当前 Lv.{rank} → 威力 ×{mul:0.00}（不含暴击与增益）";
    }
    private void SkillPage()
    {
        int i = 0;
        foreach (var realm in _game.Config.Rows("SwordLevel").OrderBy(r => r.Int("order")))
        {
            // 5 列铺满 1728：列宽 344（原 376，功能区让出竖排页签后变窄了）。
            float x = i++ * 344; string rid = realm.Text("id"); bool unlocked = _game.State.Realms.Contains(rid);
            UiKit.PanelAt(_page, x, 0, 334, 452);
            UiKit.Label(_page, realm.Text("name"), x + 18, 10, 130, 44, 29, UiKit.Gold);
            if (!unlocked) UiKit.Button(_page, $"突破 {UiKit.Number(realm.Number("cost_gold"))}", x + 150, 14, 168, 38, () => Act(() => _game.UnlockRealm(rid), "sfx_breakthrough"));
            else UiKit.Label(_page, "境界已开启", x + 168, 15, 150, 36, 18, UiKit.Jade);
            int j = 0;
            foreach (var skill in _game.Config.Skills.Values.Where(s => s.Realm == rid))
            {
                // 行距从 78 放到 108：纵向多了 136px，摊到三行正好填满，不再挤在面板上半截。
                float y = 104 + j++ * 108; string sid = skill.Id;
                // 技能名那一行也要挂提示：书页这一列只塞得下名字与 CD / 神通，具体效果只能靠它说清。
                var label = UiKit.Label(_page, "", x + 18, y, 298, 30, 20).Tip(SkillTip(skill));
                // 神通不靠冷却出手，显示 "CD 5.0s" 会让人以为它每 5 秒放一次——那 5 秒只是最短触发间隔。
                _bindings.Add(() => label.Text = skill.TriggerChance > 0
                    // 概率叠加形态（trigger_chance_step > 0）只写「15%→100%」：把"起步值"和"会长"两件事一起说清，
                    // 又塞得进这一行的宽度（"每次普攻 +5%" 的完整口径在预览页与悬停提示里）。
                    // 行内只写「神通 15%」：完整口径（它由普攻引动）在悬停提示与预览页里，见下。
                    ? $"{skill.Name}  Lv.{_game.State.Skills.GetValueOrDefault(sid)}   神通 {skill.TriggerChance:P0}"
                        + (skill.TriggerChanceStep > 0 ? "→100%" : "")
                    : $"{skill.Name}  Lv.{_game.State.Skills.GetValueOrDefault(sid)}   CD {_game.Battle.Cooldowns.GetValueOrDefault(sid):0.0}s");
                var button = UiKit.Button(_page, "", x + 18, y + 34, 298, 40, () => Act(() => _game.UpgradeSkill(sid)));
                _bindings.Add(() => button.Text = $"{(_game.State.Skills.GetValueOrDefault(sid) > 0 ? "强化" : "习得")} · {UiKit.Number(_game.SkillCost(sid))} {_game.CurrencyName("gold")}");
                button.Disabled = !unlocked;
                // 提示与技能名那一行**同一份**（`SkillTip`）。从前这里只写"描述 + 升级口径"两行，
                // 于是玩家悬停「习得/强化」按钮时看到的仍是旧样子——而那个按钮才是他真正会去悬停的。
                _bindings.Add(() => button.Tip(SkillTip(skill)));
            }
        }
    }
    private void ForgePage()
    {
        UiKit.PanelAt(_page, 0, 0, 470, 452);
        UiKit.Label(_page, "本命之剑", 24, 20, 400, 46, 29, UiKit.Gold);
        string name = _game.State.Weapon == "" ? "尚未佩剑" : _game.Config.Row("Equip", _game.State.Weapon).Text("name") + "  +" + _game.State.WeaponLevel;
        UiKit.Label(_page, name, 24, 92, 420, 54, 33);
        UiKit.Label(_page, $"武器攻击 +{UiKit.Number(_game.WeaponAttack)}   品质 {_game.State.WeaponRoll:P0}", 24, 156, 420, 42, 22, UiKit.Jade);
        if (_game.State.Weapon != "")
        {
            var w = _game.Config.Row("Equip", _game.State.Weapon);
            UiKit.Button(_page, $"淬炼 {w.Number("upgrade_cost") * (_game.State.WeaponLevel + 1):0}", 24, 232, 198, 50, () => Act(_game.Strengthen));
            UiKit.Button(_page, $"洗练 {w.Number("refine_cost"):0}", 238, 232, 198, 50, () => Act(_game.Refine));
        }
        UiKit.Label(_page, "单装备位 · 洗练品质 90%～130%", 24, 316, 420, 32, 18, UiKit.Muted);
        int i = 0;
        foreach (var r in _game.Config.Rows("Equip"))
        {
            // 1 个本命位 + 3 把可打造：470 + 3×414 = 1712，正好铺满 1728。
            float x = 486 + i++ * 414; string id = r.Text("id");
            UiKit.PanelAt(_page, x, 0, 400, 452);
            UiKit.Label(_page, r.Text("name"), x + 24, 40, 352, 52, 32, UiKit.Gold);
            UiKit.Label(_page, $"基础攻击 +{r.Number("base_atk"):0}", x + 24, 116, 344, 46, 25, UiKit.Jade);
            UiKit.Label(_page, "打造后替换当前武器\n强化等级与洗练品质重新开始", x + 24, 176, 352, 66, 18, UiKit.Muted);
            UiKit.Button(_page, $"打造并装备 · {r.Number("craft_cost"):0} {_game.CurrencyName("gold")}", x + 24, 300, 352, 54, () => Act(() => _game.Craft(id)), true);
        }
    }
    /// <summary>
    /// 参悟行的效果文案——按配置的 `effect` 渲染，而不是一律写「效果 +X%」。
    /// 剑二十三那 4 行的语义是**继承比例**（它的 `power` 根本不参与伤害），写成"伤害"会误导。
    ///
    /// 兜底是**响亮失败**，不是印一句含糊的「效果 +X%/级」：加载期虽然已经把 `effect` 校验成白名单，
    /// 但"白名单加了一个取值、这里忘了补文案"正是本工程反复踩的那类静默失败——
    /// 那时玩家看到的是一句**看起来没问题的错话**，比看到 id 更难发现。
    /// </summary>
    private static string IntentEffectText(CsvRow r) => r.Text("effect") switch
    {
        "inherit_percent" => $"继承比例 +{r.Number("value"):P0}/级",
        "damage_percent" => $"伤害 +{r.Number("value"):P0}/级",
        _ => throw new InvalidDataException($"参悟效果 '{r.Text("effect")}' 没有文案——新增 effect 时要在这里补一条"),
    };

    private void IntentPage()
    {
        string[] names = ["参悟灵石", "风之剑意", "雷之剑意", "霜之剑意", "炎之剑意"];
        for (int i = 0; i < names.Length; i++) { int tab = i - 1; UiKit.Button(_page, names[i], i * 235, 0, 220, 40, () => { _intentTab = tab; ShowPage(3); Refresh(); }, _intentTab == tab); }
        var currencies = UiKit.Label(_page, "", 1190, 0, 530, 40, 19, UiKit.Jade);
        _bindings.Add(() => currencies.Text = string.Join("   ", Enumerable.Range(0, 4).Select(i => $"{new[] { "风", "雷", "霜", "炎" }[i]} {UiKit.Number(_game.State.Amount("intent_" + i))}")));
        if (_intentTab == -1)
        {
            int i = 0;
            foreach (var r in _game.Config.Rows("contemplation"))
            {
                // 4 块灵石：4×430 = 1720，正好铺满 1728。
                float x = i++ * 430; string id = r.Text("id"), item = r.Text("item_id");
                UiKit.PanelAt(_page, x, 56, 414, 300);
                UiKit.Label(_page, r.Text("name"), x + 22, 74, 370, 44, 26, UiKit.Gold);
                UiKit.Button(_page, "◇  点击参悟", x + 22, 146, 180, 76, () => { _game.ClickOre(id); Refresh(); }, true);
                var pile = UiKit.Button(_page, "", x + 212, 146, 180, 76, () => _game.CollectIntent(item));
                pile.MouseEntered += () => { _game.CollectIntent(item); Refresh(); };
                _bindings.Add(() => pile.Text = $"移入收取 ×{_game.State.PendingIntent.GetValueOrDefault(item):0}");
                var status = UiKit.Label(_page, "", x + 22, 248, 370, 42, 18, UiKit.Muted);
                // 自动参悟（`auto_intent`）**当前没有任何修行节点承载**——用户拍板：该功能暂不投放，
                // 做到那一步再决定挂在哪。所以默认那句不能再写"修行可解锁自动参悟"（那是句空头承诺），
                // 如实说"尚未开放"；机制与消费点都留着，将来挂上节点这段文案自动就对了。
                _bindings.Add(() => status.Text = $"积存上限 {r.Number("capacity"):0}  /  "
                    + (_game.TalentBonus("auto_intent") > 0 ? $"每 {r.Number("auto_interval"):0} 秒自悟" : "自动参悟尚未开放"));
            }
            UiKit.Label(_page, "参悟时战斗继续；产物积存至上限，离开页面不会丢。", 10, 386, 1700, 30, 18, UiKit.Muted);
        }
        else
        {
            int i = 0;
            foreach (var r in _game.Config.Rows("SwordUpgrade").Where(r => r.Int("tier") == _intentTab))
            {
                // 5 列 × 3 行铺满 1728×452：330×5 = 1650，行距 100 摊开三行。
                int index = i++; float x = index % 5 * 344, y = 58 + index / 5 * 100; string id = r.Text("id");
                var b = UiKit.Button(_page, "", x, y, 330, 88, () => Act(() => _game.UpgradeIntent(id)));
                _bindings.Add(() => { int rank = _game.State.Upgrades.GetValueOrDefault(id); b.Text = $"{r.Text("name")}  {rank}/{r.Int("max_level")}\n{IntentEffectText(r)} · 消耗 {r.Number("cost") * (rank + 1):0}"; });
            }
        }
    }
    private void PetPage()
    {
        UiKit.PanelAt(_page, 0, 0, 430, 316);
        UiKit.Label(_page, "灵契 · 剑灵应召", 22, 22, 390, 44, 29, UiKit.Gold);
        UiKit.Label(_page, $"出战 {_game.State.EquippedPets.Count}/3\n每个剑灵可装 3 类增强", 22, 83, 380, 75, 24, UiKit.Jade);
        UiKit.Button(_page, $"召唤 · {_game.Config.Setting("pet_draw_cost"):0} {_game.CurrencyName("gold")}", 22, 182, 384, 54, () => Act(_game.DrawPet), true);
        double totalWeight = _game.Config.Rows("Pet").Sum(r => r.Number("weight"));
        string odds = string.Join(" · ", _game.Config.Rows("Pet").Select(r => $"{r.Text("name")}{r.Number("weight") / totalWeight:P0}"));
        UiKit.Label(_page, odds + $"\n重复返还{_game.Config.Setting("pet_duplicate_gold"):0}{_game.CurrencyName("gold")}；样例无保底", 22, 250, 390, 53, 18, UiKit.Muted);
        int i = 0;
        foreach (var r in _game.Config.Rows("Pet"))
        {
            float x = 454 + i++ * 475; string id = r.Text("id"); bool owned = _game.State.Pets.Contains(id);
            UiKit.PanelAt(_page, x, 0, 452, 316);
            UiKit.Label(_page, r.Text("name") + (owned ? "" : " · 未应召"), x + 22, 15, 400, 47, 29, UiKit.Gold);
            var equip = UiKit.Button(_page, _game.State.EquippedPets.Contains(id) ? "已出战 · 点击休息" : "出战", x + 22, 72, 406, 43, () => Act(() => _game.TogglePet(id))); equip.Disabled = !owned;
            int j = 0;
            foreach (var buff in _game.Config.Rows("PetEquip"))
            {
                string bid = buff.Text("id"); bool has = _game.State.PetBuffs.GetValueOrDefault(id, []).Contains(bid);
                var b = UiKit.Button(_page, $"{buff.Text("name")}  +{buff.Number("power"):P0}  " + (has ? "已装备" : $"{buff.Number("cost_gold"):0} {_game.CurrencyName("gold")}"), x + 22, 133 + j++ * 54, 406, 44, () => Act(() => _game.EquipPetBuff(id, bid))); b.Disabled = !owned || has;
            }
        }
    }
}

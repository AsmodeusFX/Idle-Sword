using Godot;
using IdleSword.Core;

namespace IdleSword.UI;

public partial class Main
{
    /// <summary>技能预览页：15 个法术各一个按钮，逐个对照表现与数值。</summary>
    private void PreviewPage()
    {
        var ids = PreviewSkillIds;
        var skill = _game.Config.Skills[PreviewSkillId];
        UiKit.Label(_page, "技能预览", 20, 0, 220, 36, 26, UiKit.Gold);
        UiKit.Label(_page, PreviewSummary(skill), 210, 2, 1180, 34, 19, UiKit.Jade);
        UiKit.Button(_page, "◀ 上一个", 1310, 0, 175, 36, () => { SelectPreviewSkill(_previewSkill - 1); ShowPage(_selectedTab); Refresh(); });
        UiKit.Button(_page, "下一个 ▶", 1495, 0, 175, 36, () => { SelectPreviewSkill(_previewSkill + 1); ShowPage(_selectedTab); Refresh(); });
        UiKit.Button(_page, "退出预览", 1680, 0, 180, 36, TogglePreview);
        int i = 0;
        foreach (string id in ids)
        {
            var row = _game.Config.Skills[id]; int index = i++;
            // **竖着排**：一列一个境界（第 1 列小妖、第 2 列妖将……），列内按技能书顺序自上而下。
            // `ids` 已按境界排好且每境恰好 3 个（见 core_rules.md 的「5 个境界，每境 3 个技能」），
            // 所以列 = index / 3、行 = index % 3。**若以后放宽每境技能数，这两处要改成按境界分组排行号。**
            var button = UiKit.Button(_page, row.Name, index / 3 * 376, 42 + index % 3 * 78, 356, 70,
                () => { SelectPreviewSkill(index); ShowPage(_selectedTab); Refresh(); }, id == PreviewSkillId);
            // 悬停提示里放完整口径：书页那一行只能写「15%→100%」，涨多少得在这里说清。
            button.TooltipText = skill.TriggerChanceStep > 0
                ? $"{row.Description}\n神通：每经过一次普攻，触发概率 +{skill.TriggerChanceStep:P0}，摇中后回到 {skill.TriggerChance:P0}。"
                : row.Description;
        }
        UiKit.Label(_page, skill.Description, 20, 276, 1840, 36, 19, UiKit.Muted);
    }

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
            float x = i++ * 376; string rid = realm.Text("id"); bool unlocked = _game.State.Realms.Contains(rid);
            UiKit.PanelAt(_page, x, 0, 358, 316);
            UiKit.Label(_page, realm.Text("name"), x + 18, 8, 130, 40, 29, UiKit.Gold);
            if (!unlocked) UiKit.Button(_page, $"突破 {UiKit.Number(realm.Number("cost_gold"))}", x + 154, 12, 184, 38, () => Act(() => _game.UnlockRealm(rid), "sfx_breakthrough"));
            else UiKit.Label(_page, "境界已开启", x + 180, 13, 150, 35, 18, UiKit.Jade);
            int j = 0;
            foreach (var skill in _game.Config.Skills.Values.Where(s => s.Realm == rid))
            {
                float y = 64 + j++ * 78; string sid = skill.Id;
                var label = UiKit.Label(_page, "", x + 18, y, 356, 27, 20);
                // 神通不靠冷却出手，显示 "CD 5.0s" 会让人以为它每 5 秒放一次——那 5 秒只是最短触发间隔。
                _bindings.Add(() => label.Text = skill.TriggerChance > 0
                    // 概率叠加形态（trigger_chance_step > 0）只写「15%→100%」：把"起步值"和"会长"两件事一起说清，
                    // 又塞得进这一行的宽度（"每次普攻 +5%" 的完整口径在预览页与悬停提示里）。
                    // 行内只写「神通 15%」：完整口径（它由普攻引动）在悬停提示与预览页里，见下。
                    ? $"{skill.Name}  Lv.{_game.State.Skills.GetValueOrDefault(sid)}   神通 {skill.TriggerChance:P0}"
                        + (skill.TriggerChanceStep > 0 ? "→100%" : "")
                    : $"{skill.Name}  Lv.{_game.State.Skills.GetValueOrDefault(sid)}   CD {_game.Battle.Cooldowns.GetValueOrDefault(sid):0.0}s");
                var button = UiKit.Button(_page, "", x + 18, y + 31, 320, 36, () => Act(() => _game.UpgradeSkill(sid)));
                _bindings.Add(() => button.Text = $"{(_game.State.Skills.GetValueOrDefault(sid) > 0 ? "强化" : "习得")} · {UiKit.Number(_game.SkillCost(sid))} 灵钱");
                button.Disabled = !unlocked;
                // 提示要跟着等级变（当前 Lv 与覆盖率），所以放进绑定里逐帧刷新。
                _bindings.Add(() => button.TooltipText = $"{skill.Description}\n{UpgradeTip(skill, _game.State.Skills.GetValueOrDefault(sid))}");
            }
        }
    }
    private void ForgePage()
    {
        UiKit.PanelAt(_page, 0, 0, 510, 316);
        UiKit.Label(_page, "本命之剑", 24, 15, 420, 45, 29, UiKit.Gold);
        string name = _game.State.Weapon == "" ? "尚未佩剑" : _game.Config.Row("Equip", _game.State.Weapon).Text("name") + "  +" + _game.State.WeaponLevel;
        UiKit.Label(_page, name, 24, 74, 450, 52, 33);
        UiKit.Label(_page, $"武器攻击 +{UiKit.Number(_game.WeaponAttack)}   品质 {_game.State.WeaponRoll:P0}", 24, 135, 450, 40, 22, UiKit.Jade);
        if (_game.State.Weapon != "")
        {
            var w = _game.Config.Row("Equip", _game.State.Weapon);
            UiKit.Button(_page, $"淬炼 {w.Number("upgrade_cost") * (_game.State.WeaponLevel + 1):0}", 24, 199, 218, 48, () => Act(_game.Strengthen));
            UiKit.Button(_page, $"洗练 {w.Number("refine_cost"):0}", 260, 199, 218, 48, () => Act(_game.Refine));
        }
        UiKit.Label(_page, "单装备位 · 洗练品质 90%～130%", 24, 268, 455, 32, 18, UiKit.Muted);
        int i = 0;
        foreach (var r in _game.Config.Rows("Equip"))
        {
            float x = 534 + i++ * 450; string id = r.Text("id");
            UiKit.PanelAt(_page, x, 0, 430, 316);
            UiKit.Label(_page, r.Text("name"), x + 24, 30, 380, 52, 32, UiKit.Gold);
            UiKit.Label(_page, $"基础攻击 +{r.Number("base_atk"):0}", x + 24, 99, 370, 45, 25, UiKit.Jade);
            UiKit.Label(_page, "打造后替换当前武器\n强化等级与洗练品质重新开始", x + 24, 150, 380, 65, 18, UiKit.Muted);
            UiKit.Button(_page, $"打造并装备 · {r.Number("craft_cost"):0} 灵钱", x + 24, 240, 382, 52, () => Act(() => _game.Craft(id)), true);
        }
    }
    /// <summary>
    /// 参悟行的效果文案——按配置的 `effect` 渲染，而不是一律写「效果 +X%」。
    /// 剑二十三那 4 行的语义是**继承比例**（它的 `power` 根本不参与伤害），写成"伤害"会误导；
    /// 未知取值由加载期校验拦住（见 `GameConfig` 对 `SwordUpgrade.effect` 的检查），这里给个兜底。
    /// </summary>
    private static string IntentEffectText(CsvRow r) => r.Text("effect") switch
    {
        "inherit_percent" => $"继承比例 +{r.Number("value"):P0}/级",
        "damage_percent" => $"伤害 +{r.Number("value"):P0}/级",
        _ => $"效果 +{r.Number("value"):P0}/级",
    };

    private void IntentPage()
    {
        string[] names = ["参悟灵石", "风之剑意", "雷之剑意", "霜之剑意", "炎之剑意"];
        for (int i = 0; i < names.Length; i++) { int tab = i - 1; UiKit.Button(_page, names[i], i * 235, 0, 220, 40, () => { _intentTab = tab; ShowPage(3); Refresh(); }, _intentTab == tab); }
        var currencies = UiKit.Label(_page, "", 1190, 0, 665, 40, 19, UiKit.Jade);
        _bindings.Add(() => currencies.Text = string.Join("   ", Enumerable.Range(0, 4).Select(i => $"{new[] { "风", "雷", "霜", "炎" }[i]} {UiKit.Number(_game.State.Amount("intent_" + i))}")));
        if (_intentTab == -1)
        {
            int i = 0;
            foreach (var r in _game.Config.Rows("contemplation"))
            {
                float x = i++ * 469; string id = r.Text("id"), item = r.Text("item_id");
                UiKit.PanelAt(_page, x, 56, 451, 217);
                UiKit.Label(_page, r.Text("name"), x + 22, 67, 400, 42, 26, UiKit.Gold);
                UiKit.Button(_page, "◇  点击参悟", x + 22, 121, 185, 65, () => { _game.ClickOre(id); Refresh(); }, true);
                var pile = UiKit.Button(_page, "", x + 225, 121, 203, 65, () => _game.CollectIntent(item));
                pile.MouseEntered += () => { _game.CollectIntent(item); Refresh(); };
                _bindings.Add(() => pile.Text = $"移入收取 ×{_game.State.PendingIntent.GetValueOrDefault(item):0}");
                var status = UiKit.Label(_page, "", x + 22, 206, 405, 42, 18, UiKit.Muted);
                _bindings.Add(() => status.Text = $"积存上限 {r.Number("capacity"):0}  /  " + (_game.TalentBonus("auto_intent") > 0 ? $"每 {r.Number("auto_interval"):0} 秒自悟" : "修行可解锁自动参悟"));
            }
            UiKit.Label(_page, "参悟时战斗继续；离开页面仍在线自悟，产物积存至上限。", 10, 282, 1780, 30, 18, UiKit.Muted);
        }
        else
        {
            int i = 0;
            foreach (var r in _game.Config.Rows("SwordUpgrade").Where(r => r.Int("tier") == _intentTab))
            {
                int index = i++; float x = index % 5 * 376, y = 58 + index / 5 * 86; string id = r.Text("id");
                var b = UiKit.Button(_page, "", x, y, 356, 75, () => Act(() => _game.UpgradeIntent(id)));
                _bindings.Add(() => { int rank = _game.State.Upgrades.GetValueOrDefault(id); b.Text = $"{r.Text("name")}  {rank}/{r.Int("max_level")}\n{IntentEffectText(r)} · 消耗 {r.Number("cost") * (rank + 1):0}"; });
            }
        }
    }
    private void PetPage()
    {
        UiKit.PanelAt(_page, 0, 0, 430, 316);
        UiKit.Label(_page, "灵契 · 剑灵应召", 22, 22, 390, 44, 29, UiKit.Gold);
        UiKit.Label(_page, $"出战 {_game.State.EquippedPets.Count}/3\n每个剑灵可装 3 类增强", 22, 83, 380, 75, 24, UiKit.Jade);
        UiKit.Button(_page, $"召唤 · {_game.Config.Setting("pet_draw_cost"):0} 灵钱", 22, 182, 384, 54, () => Act(_game.DrawPet), true);
        double totalWeight = _game.Config.Rows("Pet").Sum(r => r.Number("weight"));
        string odds = string.Join(" · ", _game.Config.Rows("Pet").Select(r => $"{r.Text("name")}{r.Number("weight") / totalWeight:P0}"));
        UiKit.Label(_page, odds + $"\n重复返还{_game.Config.Setting("pet_duplicate_gold"):0}灵钱；样例无保底", 22, 250, 390, 53, 18, UiKit.Muted);
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
                var b = UiKit.Button(_page, $"{buff.Text("name")}  +{buff.Number("power"):P0}  " + (has ? "已装备" : $"{buff.Number("cost_gold"):0} 灵钱"), x + 22, 133 + j++ * 54, 406, 44, () => Act(() => _game.EquipPetBuff(id, bid))); b.Disabled = !owned || has;
            }
        }
    }
}

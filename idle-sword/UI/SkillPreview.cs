using Godot;
using IdleSword.Core;
using IdleSword.Features;

namespace IdleSword.UI;

/// <summary>
/// 技能预览模式：用独立会话逐个播放 15 个剑诀，供设计审核对照。
/// 预览会话不写存档、不参与玩家进度，退出后不留痕迹；预览期间主线挂机暂停，
/// 避免"看技能"时后台还在推进关卡。
/// </summary>
public partial class Main
{
    private GameSession? _preview;
    private int _previewSkill;
    private double _previewClock;
    // 靶子距离取 520：落在停步距离 640 之内（角色站定不乱跑），又在全部剑诀射程 950 之内。
    private const double PreviewTargetDistance = 520;
    private const double PreviewTargetHp = 2000;
    // 每 1.8 秒重置所选剑诀冷却，让 12 秒冷却的大招也能快速反复观察。
    private const double PreviewRecast = 1.8;

    private GameSession Active => _preview ?? _game;
    // 预览的翻页与网格顺序：**按境界 1→5** 排，境界内保持技能书行序（= Config.Skills 的插入序；LINQ 的 OrderBy 是稳定排序）。
    // 不按 id 字典序——那是 `skill_01, skill_02, skill_04, …` 的排法，又跳境界又跳编号，逐个核对时很别扭。
    private List<string> PreviewSkillIds => _game.Config.Skills.Values
        .OrderBy(s => _game.Config.Row("SwordLevel", s.Realm).Int("order"))
        .Select(s => s.Id).ToList();
    private string PreviewSkillId => PreviewSkillIds[_previewSkill];

    private void TogglePreview()
    {
        if (_preview is null)
        {
            // 固定种子：同一剑诀每次预览的表现一致，便于对照。
            // 关掉普攻：预览是逐个剑诀的对照台，每秒一发的飞剑只会往画面里混入不属于所选剑诀的东西。
            _preview = new GameSession(_game.Config, seed: 1) { BasicAttackEnabled = false };
            SelectPreviewSkill(0);
        }
        else _preview = null;
        _battle.Session = Active;
        ShowPage(_selectedTab); Refresh();
    }

    private void SelectPreviewSkill(int index)
    {
        if (_preview is null) return;
        var ids = PreviewSkillIds;
        _previewSkill = Math.Clamp(index, 0, ids.Count - 1);
        string current = ids[_previewSkill];
        // 只保留当前剑诀，避免其他技能的特效混进来，失去对照意义。
        foreach (string id in ids) _preview.State.Skills[id] = id == current ? 1 : 0;
        // 剑二十三是个例外：它本身不产生任何效果，只是"让本体放出的剑诀多一份"。
        // 不给它配一个搭档剑诀，预览里就只剩一个站着不动的分身，什么也演示不出来。
        if (_preview.Config.Skills[current].Secondary == "mirror" && _preview.State.Skills.ContainsKey("skill_01"))
            _preview.State.Skills["skill_01"] = 1;
        _preview.Battle.Cooldowns.Clear();
        // 清掉上一招的全部残留（飞行效果、玩家增益、飘字）：否则切技能后画面里混着两招，失去逐个对照的意义。
        _preview.Effects.Clear();
        _preview.ClearBuffs();
        _battle.ResetTransient();
        _previewClock = PreviewRecast;
        PreparePreviewField();
    }

    /// <summary>固定靶场：一个不移动、不反击、打不死的目标，并关闭刷怪。</summary>
    private void PreparePreviewField()
    {
        if (_preview is null) return;
        _preview.Battle.Spawns.Clear();
        // 把当前格的刷怪点标记为已越过，使 TickSpawns 不补怪（清空会被 ActivateCell 重新激活）。
        _preview.Battle.Spawns[_preview.Battle.Cell] = new() { Passed = true };
        // 原地清空不经过 BattleView 的换场守卫，不重置的话旧靶子会被当成击杀、多播一次死亡音。
        _preview.Battle.Enemies.Clear();
        _battle.ResetTransient();
        _preview.Battle.PlayerHp = _preview.MaxHp;
        var monster = _preview.Config.Monsters["slime"];
        _preview.Battle.Enemies.Add(new()
        {
            Id = _preview.Battle.NextEnemyId++, MonsterId = monster.Id, Kind = monster.Kind,
            X = _preview.Battle.PlayerX + PreviewTargetDistance,
            Hp = PreviewTargetHp, MaxHp = PreviewTargetHp, Atk = 0, AttackTimer = 999,
        });
    }

    private void TickPreview(double dt)
    {
        if (_preview is null) return;
        _previewClock += dt;
        if (_previewClock >= PreviewRecast)
        {
            _previewClock = 0;
            _preview.Battle.Cooldowns.Clear();
            // 清完冷却要让释放音的观测点先看到"冷却为 0"这一帧：TrackSkillCasts 逐 Step 观测 0 → 正 的跳变，
            // 而本步的 Step 紧接着就会把技能放出去并写回冷却——不在这里补一次观测，那个跳变永远发生在同一步之内，
            // 预览模式下就一声释放音都不会响（真机上表现为"看技能时没有音效"）。
            TrackSkillCasts(Active);
            // 真诀不在冷却到点自动释放（要等普攻按概率摇中），而预览里普攻是关掉的。
            // 不显式放一次，它们在对照模式下永远不出手，等于看不到。
            if (_preview.Config.Skills[PreviewSkillId].TriggerChance > 0) _preview.ForceRelease(PreviewSkillId);
        }
        var target = _preview.Battle.Enemies.FirstOrDefault(e => e.Hp > 0);
        if (target is null) { PreparePreviewField(); return; }
        // 每步重新钉住靶子：位置、免伤、不反击，保证 15 个剑诀面对完全相同的对照条件。
        target.X = _preview.Battle.PlayerX + PreviewTargetDistance;
        target.Atk = 0; target.AttackTimer = 999;
        if (target.Hp < target.MaxHp * .25) target.Hp = target.MaxHp;
        _preview.Battle.Spawns.Clear();
        _preview.Battle.Spawns[_preview.Battle.Cell] = new() { Passed = true };
    }

    /// <summary>QA 用：以固定步长推进预览若干帧，使截图不依赖真实帧率。</summary>
    private void AdvancePreview(int frames)
    {
        if (_preview is null) return;
        double step = _game.Config.Setting("fixed_step");
        // 与 _Process 的真实路径保持一致：逐 Step 观测技能冷却，QA 推进也才会触发释放音。
        for (int i = 0; i < frames; i++) { TickPreview(step); _preview.Step(step); TrackSkillCasts(Active); }
    }

    /// <summary>一行摘要：给审核时快速核对数值，界面不复述配置表以外的内容。</summary>
    private string PreviewSummary(SkillDef skill)
    {
        string secondary = skill.Secondary switch
        {
            "" => "无次级效果",
            "pierce" => "穿透",
            "multi" => $"多重 ×{skill.SecondaryValue:0}",
            "slow" => $"减速 {skill.SecondaryValue:P0} / {skill.SecondaryDuration:0.#}s",
            "chill" => $"寒冷 减速 {skill.SecondaryValue:P0} / {skill.SecondaryDuration:0.#}s",
            "stun" => $"眩晕 {skill.SecondaryDuration:0.#}s",
            "dot" => $"灼烧 {skill.SecondaryValue:P0}/秒 / {skill.SecondaryDuration:0.#}s",
            "vulnerable" => $"易伤 +{skill.SecondaryValue:P0} / {skill.SecondaryDuration:0.#}s",
            "lifesteal" => $"吸血 {skill.SecondaryValue:P0} / {skill.SecondaryDuration:0.#}s",
            "execute" => $"斩杀 阈值 {skill.SecondaryValue:P0}",
            "shield" => $"护盾 攻击×{skill.SecondaryValue:0.##} / {skill.SecondaryDuration:0.#}s",
            "regen" => $"回血 {skill.SecondaryValue:P0}/秒 / {skill.SecondaryDuration:0.#}s",
            "haste" => $"攻速 +{skill.SecondaryValue:P0} / {skill.SecondaryDuration:0.#}s",
            "crit_reduce" => $"暴击 +{skill.SecondaryValue:P0} / {skill.SecondaryDuration:0.#}s · 暴击缩冷却 {skill.SecondaryExtra:0.##}s",
            // 影分身：摘要里要写清"同步复制、继承多少"，这两个数是这个技能的全部内容。
            "mirror" => $"影分身 同步复制剑诀 / 本体伤害 {skill.SecondaryValue:P0} 起 · {skill.SecondaryDuration:0.#}s",
            _ => skill.Secondary,
        };
        // 飞行形态摘要：审核弹道时最需要核对的就是"几支、怎么飞、范围多大、出剑节奏"。
        // 这一行要与翻页按钮抢宽度，所以用词从简；有形态时不再重复"无次级效果"（三个形态都没次级效果）。
        string speed = skill.Speed > 0 ? $" · 速度 {skill.Speed:0}" : "";
        string interval = skill.VolleyInterval > 0 ? $" · 错时 {skill.VolleyInterval:0.##}" : "";
        string shape = skill.Trajectory switch
        {
            "hover_homing" => $"悬浮追踪 ×{skill.ProjectileCount} · 停 {skill.HoverTime:0.##}s{interval}",
            "sky_drop" => $"空降剑阵 ×{skill.ProjectileCount} · 间距 {skill.Spread:0} · 落点 {skill.AoeRadius:0}"
                + $" · 停 {skill.HoverTime:0.##}s · 高差 {skill.SpawnJitter:0}{interval}",
            "arc_homing" => $"弧线 ×{skill.ProjectileCount} · 弧高 {skill.ArcMin:0}~{skill.ArcMax:0}{interval}{speed}",
            "line_shot" => $"肩侧平射 ×{skill.ProjectileCount} · 间距 {skill.Spread:0} · 命中即散{interval}{speed}"
                + (skill.PierceChance > 0 ? $" · 概率穿透 {skill.PierceChance:P0}" : ""),
            "line_pierce" => $"平射贯穿 ×{skill.ProjectileCount} · 间距 {skill.Spread:0}{interval}{speed}",
            _ => skill.Kind == "projectile" && skill.ProjectileCount > 1 ? $"直线追踪 ×{skill.ProjectileCount}{interval}" : "",
        };
        string realm = _game.Config.Row("SwordLevel", skill.Realm).Text("name");
        string tail = shape == "" ? secondary : skill.Secondary == "" ? shape : $"{secondary} · {shape}";
        // 真诀的出手时机由普攻概率决定，配置里的 cooldown 只是最短触发间隔；不点明的话"冷却 5s"会被读错。
        string trigger = skill.TriggerChance > 0
            ? $"普攻触发 {skill.TriggerChance:P0}{(skill.TriggerChanceStep > 0 ? $" 起 · 每次普攻 +{skill.TriggerChanceStep:P0}" : "")} · 最短间隔 {skill.Cooldown:0.#}s"
            : $"冷却 {skill.Cooldown:0.#}s";
        return $"{realm} · {skill.Kind} · {trigger} · 射程 {skill.Range:0} · 威力 ×{skill.Power:0.##} · {tail}";
    }
}

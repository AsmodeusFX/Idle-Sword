using Godot;
using IdleSword.Core;
using IdleSword.Features;

namespace IdleSword.UI;

/// <summary>
/// **技能预览**（GM →「技能预览」）：一块**专用的全屏工具页**，逐个播放 15 个法术供设计审核对照。
///
/// 三条边界，每一条都是为了让"正规玩法的改动"碰不到这个工具：
///
/// 1. **自己的战斗视口**。页面持有**自己的 `BattleView` 实例**，不复用主场景那一个——
///    主场景的视口位置 / 尺寸 / 层级怎么改都与这里无关。
/// 2. **沙盒会话**（`GameSession.SandboxMode`）：世界静止、只有战斗结算在跑；
///    主线挂机在预览期间**完全暂停**（`Main` 的推进分支只推这一个会话），预览状态不落盘。
/// 3. **自己的页面槽位**。不走玩家的 `_page`（不复用 `ShowPage` / `_selectedTab` / `_bindings`），
///    所以底栏页签、玩家页面的重排都不会影响它。
///
/// > ⚠️ **第 2 条是这一页重建的原因**。从前它靠 UI 侧打补丁来冻结世界（关普攻 + 把当前格标 `Passed` +
/// > 每步重钉靶子），而补丁挡不住"正规改动的连带影响"：预览会话是一份**全新开局状态**，
/// > 没点「剑气」⇒ 近战停步只有 **120**，而靶子钉在身前 **520**（那个距离是按远程停步 640 定的）——
/// > 于是角色一路前进，走进新格就激活新刷怪点、当拍刷怪。现在"世界不动"是会话自己的保证。
/// </summary>
public partial class Main
{
    /// <summary>练度板面：预览用哪一份玩家状态。</summary>
    private enum PreviewLevel { Fresh, Maxed, Follow }

    /// <summary>靶场：场上放哪几种层的固定靶。</summary>
    private enum PreviewField { Ground, Air, Mixed }

    private GameSession? _preview;
    private int _previewSkill;
    private double _previewClock;
    private bool _previewPaused;
    private PreviewLevel _previewLevel = PreviewLevel.Maxed;
    private PreviewField _previewField = PreviewField.Mixed;

    /// <summary>沙盒的起点：与关卡起点一致，于是地面装饰与镜头读起来跟实战一样。</summary>
    private const double PreviewPlayerX = GameSession.LevelStartX;
    /// <summary>
    /// 两只靶的距离。**近靶 380 是刻意的**：剑罡护体的环绕飞剑出手范围是 `guard_range` = 400，
    /// 靶子摆远了那一半内容在预览里永远不出手。远靶 640 = 远程 `stop_range`，也就是玩家真实交战的位置
    /// （全部法术 `range` = 950，两个距离都够得着）。
    /// </summary>
    private const double PreviewNearX = 380, PreviewFarX = 640;
    /// <summary>每 1.8 秒重置所选法术冷却，让 12 秒冷却的大招也能快速反复观察。</summary>
    private const double PreviewRecast = 1.8;
    private const float PreviewViewTop = 70, PreviewViewHeight = 440;

    /// <summary>该技能一次施法大约打掉的血量份额：靶子血量取"5 发打空"，
    /// 于是血条每一发都看得见变化、BattleView 的"重击"阈值（≥12% 血上限）也才有意义。
    /// 不取固定血量的原因：15 个法术的威力跨了三个数量级（1.0 ~ 279），
    /// 而练度开关又会把攻击力再抬一个量级——固定血量在某一档下必然要么"打不动"要么"一发秒"。</summary>
    private const double PreviewHpPerCast = 5;

    /// <summary>工具页建过没有（`null!` 字段上做可空判断容易被编译器唠叨，所以单开一个 bool）。</summary>
    private bool _previewBuilt;
    private Control _previewRoot = null!;
    private BattleView _previewBattle = null!;
    private Label _previewName = null!;
    private Label _previewParams = null!;
    private Label _previewAside = null!;
    private Label _previewStatus = null!;
    private Button _previewPause = null!;
    /// <summary>该沙盒里每个法术的等级（`SelectPreviewSkill` 把其余的置 0，只留当前这一式）。</summary>
    private readonly Dictionary<string, int> _previewRanks = [];
    /// <summary>工具页自己的绑定（不走玩家的 `_bindings`）。</summary>
    private readonly List<Action> _previewBindings = [];

    private GameSession Active => _preview ?? _game;

    // 预览的翻页与网格顺序：**按境界 1→5** 排，境界内保持技能书行序（= Config.Skills 的插入序；LINQ 的 OrderBy 是稳定排序）。
    // 不按 id 字典序——那是 `skill_01, skill_02, skill_04, …` 的排法，又跳境界又跳编号，逐个核对时很别扭。
    private List<string> PreviewSkillIds => _game.Config.Skills.Values
        .OrderBy(s => _game.Config.Row("SwordLevel", s.Realm).Int("order"))
        .Select(s => s.Id).ToList();
    private string PreviewSkillId => PreviewSkillIds[_previewSkill];

    public void TogglePreview()
    {
        if (_preview is null) OpenPreview(); else ClosePreview();
    }

    private void OpenPreview()
    {
        if (!_previewBuilt) { BuildPreviewPage(); _previewBuilt = true; }
        BuildSandbox();
        _previewRoot.Visible = true;
        SelectPreviewSkill(0);
        RefreshPreviewPage();
        PlaySfx("sfx_panel");
    }

    private void ClosePreview()
    {
        _preview = null;
        _previewRanks.Clear();
        _previewPaused = false;
        if (_previewBuilt) _previewRoot.Visible = false;
        PlaySfx("sfx_panel");
    }

    /// <summary>
    /// 按当前练度开关造一个**沙盒会话**。固定种子：同一法术每次预览的表现一致，便于对照。
    /// 关掉普攻：预览是逐个法术的对照台，每秒一发的飞剑只会往画面里混入不属于所选法术的东西。
    /// </summary>
    private void BuildSandbox()
    {
        PlayerState state = _previewLevel switch
        {
            // 跟随当前存档：**必须深拷贝**。沙盒会推进、会结算（靶子被打死就掉钱、写解锁标记），
            // 共享引用一开预览就污染真实进度。
            PreviewLevel.Follow => SaveStore.Clone(_game.State),
            _ => new PlayerState(),
        };
        var session = new GameSession(_game.Config, state, seed: 1)
        {
            BasicAttackEnabled = false,
            SandboxMode = true,
        };
        if (_previewLevel == PreviewLevel.Maxed) GrantPreviewMax(session);
        // 场地自建：沙盒不推进关卡，所以这里直接把 Battle 换成一个干净的靶场（`Battle` 是公开可写属性）。
        session.State.Battle = new BattleState { LevelId = _game.Config.Levels[0].Id, PlayerX = PreviewPlayerX };
        session.State.Battle.PlayerHp = session.MaxHp;

        _preview = session;
        _previewSkill = 0;
        _previewClock = 0;
        _previewPaused = false;
        // 记下每个法术在这一档练度下的等级：切技能时只把**当前这一式**留下，其余置 0（避免别的技能的特效混进来）。
        _previewRanks.Clear();
        foreach (string id in PreviewSkillIds) _previewRanks[id] = Math.Max(1, session.State.Skills.GetValueOrDefault(id));
        _previewBattle.Session = session;
        _previewBattle.ResetTransient();
    }

    /// <summary>满配板面：全系统解锁 + 自动出手 + 远程 + 一把好武器 + 法术等级取曲线里的末期期望（32 级）。
    /// 全部写在**沙盒会话自己的状态**上——这份状态永不落盘，也永不回写玩家进度。</summary>
    private void GrantPreviewMax(GameSession session)
    {
        session.UnlockAllSystems();
        session.State.Talents["t_auto"] = 1;
        session.State.Talents["t_ranged"] = 1;
        session.GrantAllCurrencies();
        session.Craft("sword_jade");
        foreach (string id in PreviewSkillIds) session.State.Skills[id] = 32;
    }

    private void SelectPreviewSkill(int index)
    {
        if (_preview is null) return;
        var ids = PreviewSkillIds;
        _previewSkill = Math.Clamp(index, 0, ids.Count - 1);
        string current = ids[_previewSkill];
        // 只保留当前法术，避免其他技能的特效混进来，失去对照意义；等级取该沙盒里原本的等级。
        foreach (string id in ids) _preview.State.Skills[id] = id == current ? _previewRanks.GetValueOrDefault(id, 1) : 0;
        // 剑二十三是个例外：它本身不产生任何效果，只是"让本体放出的法术多一份"。
        // 不给它配一个搭档法术，预览里就只剩一个站着不动的分身，什么也演示不出来。
        if (_preview.Config.Skills[current].Secondary == "mirror")
            _preview.State.Skills["skill_01"] = _previewRanks.GetValueOrDefault("skill_01", 1);
        _preview.Battle.Cooldowns.Clear();
        // 清掉上一招的全部残留（飞行效果、玩家增益、飘字）：否则切技能后画面里混着两招，失去逐个对照的意义。
        _preview.Effects.Clear();
        _preview.ClearBuffs();
        _previewBattle.ResetTransient();
        _previewClock = PreviewRecast;
        PreparePreviewField();
        RefreshPreviewPage();
    }

    /// <summary>
    /// 摆靶场：固定靶，不移动、不反击、打不死，15 个法术面对完全相同的对照条件。
    /// **位置由这里一次定下，此后不再重钉**——沙盒保证玩家不会移动（见 `GameSession.SandboxMode`），
    /// 所以"靶子被扯着走"那种现象从结构上不存在了。
    /// </summary>
    private void PreparePreviewField()
    {
        if (_preview is null) return;
        _preview.Battle.Enemies.Clear();
        _previewBattle.ResetTransient();
        _preview.Battle.Spawns.Clear();
        double hp = PreviewTargetHp();
        void Place(string monsterId, double offset)
        {
            var m = _preview.Config.Monsters[monsterId];
            _preview.Battle.Enemies.Add(new()
            {
                Id = _preview.Battle.NextEnemyId++, MonsterId = m.Id, Kind = m.Kind,
                X = PreviewPlayerX + offset, Hp = hp, MaxHp = hp,
                Atk = 0, AttackTimer = 999, StunUntil = 1e9,   // 冻结移动与出手；不影响被选中
            });
        }
        if (_previewField is PreviewField.Ground or PreviewField.Mixed) Place("slime", PreviewNearX);
        if (_previewField is PreviewField.Air or PreviewField.Mixed) Place("bat", PreviewFarX);
        if (_previewField == PreviewField.Air) Place("bat", PreviewNearX);
    }

    /// <summary>靶子血量：该法术"5 发打空"的量（口径见 `PreviewHpPerCast`）。</summary>
    private double PreviewTargetHp()
    {
        var skill = _game.Config.Skills[PreviewSkillId];
        double dmg = DamageFormula.Dmg1(_preview!.Attack, SkillRateAt(skill, _preview.State.Skills.GetValueOrDefault(skill.Id)), skill.SkillFlat);
        return Math.Max(2000, dmg * PreviewHpPerCast);
    }

    /// <summary>
    /// 每步的场地维护。**只维持"不反击"与血条**——位置与免死由摆场时的属性 + 沙盒模式保证，
    /// 所以这里不再需要"重钉靶子"（那正是从前把靶子拴在玩家身上、看起来在跟着走的写法）。
    /// </summary>
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
            // 神通不在冷却到点自动释放（要等普攻按概率摇中），而预览里普攻是关掉的。
            // 不显式放一次，它们在对照模式下永远不出手，等于看不到。
            if (_preview.Config.Skills[PreviewSkillId].TriggerChance > 0) _preview.ForceRelease(PreviewSkillId);
        }
        if (!_preview.Battle.Enemies.Any(e => e.Hp > 0)) { PreparePreviewField(); return; }
        foreach (var e in _preview.Battle.Enemies)
        {
            e.Atk = 0; e.AttackTimer = 999; e.StunUntil = 1e9;
            // 血量掉到 1/4 以下就补满：靶子是"打不死"的对照物，不该被一发大招清场。
            if (e.Hp < e.MaxHp * .25) e.Hp = e.MaxHp;
        }
    }

    /// <summary>QA 用：以固定步长推进预览若干帧，使截图不依赖真实帧率。</summary>
    private void AdvancePreview(int frames)
    {
        if (_preview is null) return;
        double step = _game.Config.Setting("fixed_step");
        // 与 _Process 的真实路径保持一致：逐 Step 观测技能冷却，QA 推进也才会触发释放音。
        for (int i = 0; i < frames; i++) { TickPreview(step); _preview.Step(step); TrackSkillCasts(Active); }
    }

    // ────────────────────────────── 页面 ──────────────────────────────

    /// <summary>
    /// 页面外壳照 `LevelEditor` / `TalentEditor`：根 Control 1920×1080 + **不透明**背板 + `Visible` 切换。
    /// 布局全部落在 1920 之内（从前玩家页那版把化神三个技能、描述行与退出按钮都推出去了 108px）。
    /// </summary>
    private void BuildPreviewPage()
    {
        _previewRoot = new Control { MouseFilter = Control.MouseFilterEnum.Ignore, Visible = false };
        UiKit.Place(_previewRoot, 0, 0, 1920, 1080);
        AddChild(_previewRoot);
        var backdrop = new ColorRect { Color = new Color(0.02f, 0.04f, 0.06f) };
        UiKit.Place(backdrop, 0, 0, 1920, 1080); _previewRoot.AddChild(backdrop);

        // ── 头部 ──
        UiKit.Label(_previewRoot, "技能预览", 24, 14, 180, 36, 26, UiKit.Gold);
        _previewName = UiKit.Label(_previewRoot, "", 210, 20, 250, 30, 19, UiKit.Jade);
        UiKit.Label(_previewRoot, "练度", 468, 20, 44, 30, 19, UiKit.Muted);
        Segment(_previewRoot, 516, 14, ["开局", "满配", "跟随存档"], (int)_previewLevel, i =>
        {
            _previewLevel = (PreviewLevel)i;
            BuildSandbox();
            SelectPreviewSkill(_previewSkill);
        });
        UiKit.Label(_previewRoot, "靶场", 908, 20, 44, 30, 19, UiKit.Muted);
        Segment(_previewRoot, 956, 14, ["纯地面", "纯空中", "各一只"], (int)_previewField, i =>
        {
            _previewField = (PreviewField)i;
            PreparePreviewField();
            RefreshPreviewPage();
        });
        UiKit.Button(_previewRoot, "关闭", 1780, 14, 116, 36, ClosePreview);

        // ── 战斗视口：**工具自己的实例**（主场景那个完全不受影响）──
        _previewBattle = new BattleView();
        // 命中与击杀音效在主场景那边是 Main 订阅 `_battle` 的信号拿到的；这里不订阅就没有音效。
        _previewBattle.HitLanded += heavy => PlaySfx(heavy ? "sfx_hit_heavy" : "sfx_hit");
        _previewBattle.EnemyDefeated += () => PlaySfx("sfx_kill");
        UiKit.Place(_previewBattle, 0, PreviewViewTop, 1920, PreviewViewHeight);
        _previewRoot.AddChild(_previewBattle);

        // ── 技能网格：一列一个境界、列内按技能书顺序，全部落在 1920 之内 ──
        var ids = PreviewSkillIds;
        for (int i = 0; i < ids.Count; i++)
        {
            int index = i;
            var skill = _game.Config.Skills[ids[i]];
            var button = UiKit.Button(_previewRoot, skill.Name, 24 + index / 3 * 376, 520 + index % 3 * 74, 366, 64,
                () => SelectPreviewSkill(index), index == _previewSkill);
            // 每个按钮显示**它自己**那一式的内容（从前这里误用了"当前选中"那个变量，15 个提示长得一样）。
            _previewBindings.Add(() => button.Tip(SkillTip(skill)));
        }

        // ── 关键参数：常驻铺开（不再靠悬停）──
        UiKit.Label(_previewRoot, "关键参数", 24, 748, 200, 30, 19, UiKit.Muted);
        _previewParams = UiKit.Label(_previewRoot, "", 24, 782, 760, 220, 19, UiKit.Text);
        _previewAside = UiKit.Wrapped(_previewRoot, "", 800, 748, 1096, 254, 19, UiKit.Muted);

        // ── 播放控制 ──
        _previewPause = UiKit.Button(_previewRoot, "", 24, 1012, 140, 36, () =>
        {
            _previewPaused = !_previewPaused;
            RefreshPreviewPage();
        });
        UiKit.Button(_previewRoot, "重播一次", 176, 1012, 140, 36, () =>
        {
            _previewClock = PreviewRecast;
            RefreshPreviewPage();
        });
        _previewStatus = UiKit.Label(_previewRoot, "", 336, 1018, 900, 30, 19, UiKit.Muted);
    }

    /// <summary>三选一的小控件：选中的那个用强调色（与编辑器里的页签同一手法）。</summary>
    private void Segment(Control parent, float x, float y, string[] labels, int selected, Action<int> pick)
    {
        for (int i = 0; i < labels.Length; i++)
        {
            int index = i;
            UiKit.Button(parent, labels[i], x + i * 132, y, 124, 36, () => pick(index), index == selected);
        }
    }

    /// <summary>回填工具页的文字。**只改文字、不重建控件**（与伤害面板 / 属性面板同一条纪律）。</summary>
    private void RefreshPreviewPage()
    {
        if (_preview is null || !_previewBuilt || !_previewRoot.Visible) return;
        var skill = _game.Config.Skills[PreviewSkillId];
        int rank = _preview.State.Skills.GetValueOrDefault(skill.Id);
        _previewName.Text = $"{_previewSkill + 1}/{PreviewSkillIds.Count}　{_game.Config.Row("SwordLevel", skill.Realm).Text("name")} · {skill.Name}";
        double rate = SkillRateAt(skill, rank), next = SkillRateAt(skill, rank + 1);
        _previewParams.Text = SkillText.ParamsBlock(skill, _preview.Attack, rate, next);
        _previewAside.Text = $"{skill.Description}\n\n{UpgradeTip(skill, rank)}\n\n"
            + $"场上 {_preview.Battle.Enemies.Count} 个固定靶（不移动 / 不反击 / 打不死）";
        _previewPause.Text = _previewPaused ? "继续" : "暂停";
        _previewStatus.Text = _previewPaused ? "已暂停（步进停止，只剩画面）" : $"每 {PreviewRecast:0.#} 秒自动重放所选法术";
        foreach (var update in _previewBindings) update();
    }

    // ── 自检钩子 ──
    /// <summary>自检用：预览页开着没有。</summary>
    internal bool PreviewOpenForCheck() => _previewBuilt && _previewRoot.Visible;
    /// <summary>自检用：沙盒里"世界有没有动"的三个读数（玩家坐标 / 格号 / 敌人数量）。</summary>
    internal (double PlayerX, int Cell, int Enemies) SandboxForCheck() =>
        (_preview!.Battle.PlayerX, _preview.Battle.Cell, _preview.Battle.Enemies.Count);
    /// <summary>自检用：工具页最右 / 最下的控件边界，用来断言"没有超框"。</summary>
    internal (float Right, float Bottom) PreviewBoundsForCheck()
    {
        float right = 0, bottom = 0;
        if (!_previewBuilt) return (0, 0);
        foreach (var child in _previewRoot.GetChildren())
        {
            if (child is not Control c) continue;
            right = Math.Max(right, c.Position.X + c.Size.X);
            bottom = Math.Max(bottom, c.Position.Y + c.Size.Y);
        }
        return (right, bottom);
    }
}

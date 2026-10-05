using Godot;

namespace IdleSword.UI;

/// <summary>
/// GM 面板：调试专用的一小撮开关。与设置面板同一个套路——按需构建、置于最上层、背板拦截点击。
/// 它只调**会话态**（`GameSession` 上的几个调试开关）与发放资源，不碰存档格式，也不污染正式数值：
/// 波次数量、怪物倍率在 `wave.csv` / `monster.csv` 里都保持正常，测试时要多少只、多厚，在这儿临时调。
/// </summary>
public partial class Main
{
    private Control? _gmRoot;
    private Label _waveBonusValue = null!, _monsterHpValue = null!, _monsterAtkValue = null!;
    private Button _invincibleButton = null!;
    /// <summary>普攻形态的对照开关（近战 / 远程），见 `GameSession.MeleeBasic`。</summary>
    private Button _meleeButton = null!;
    /// <summary>技能预览的按钮也在 GM 面板里（主界面那条横带撤掉后搬过来的）。</summary>
    private Button? _previewToggle;
    private string InvincibleText => _game.PlayerInvincible ? "无敌：开" : "无敌：关";
    private string MeleeText => _game.MeleeBasic ? "普攻：近战" : "普攻：远程";
    // 倍率走一档一档的台阶而不是 ±1：测"怪太脆"通常要跳到 ×5 / ×10，逐 1 加太磨人。
    private static readonly double[] ScaleSteps = [.5, 1, 2, 3, 5, 8, 12, 20, 50];

    /// <summary>在当前值所在的台阶上挪一格（值被手改过就落到最近的档）。</summary>
    private static double StepScale(double current, int direction)
    {
        int at = Array.IndexOf(ScaleSteps, current);
        if (at < 0) at = Array.FindIndex(ScaleSteps, v => v >= current);
        if (at < 0) at = ScaleSteps.Length - 1;
        return ScaleSteps[Math.Clamp(at + direction, 0, ScaleSteps.Length - 1)];
    }

    private void ToggleGm()
    {
        if (_gmRoot is not null && _gmRoot.Visible) CloseGm();
        else OpenGm();
    }

    private void OpenGm()
    {
        if (_gmRoot is null) BuildGm();
        RefreshGm();
        _gmRoot!.Visible = true;
        PlaySfx("sfx_panel");
    }

    private void CloseGm()
    {
        if (_gmRoot is null || !_gmRoot.Visible) return;
        _gmRoot.Visible = false;
        PlaySfx("sfx_panel");
    }

    private void BuildGm()
    {
        _gmRoot = new Control { Visible = false }; UiKit.Place(_gmRoot, 0, 0, 1920, 1080); AddChild(_gmRoot);
        var backdrop = new ColorRect { Color = new Color(0, 0, 0, .62f) }; UiKit.Place(backdrop, 0, 0, 1920, 1080); _gmRoot.AddChild(backdrop);
        UiKit.PanelAt(_gmRoot, 700, 250, 520, 672);
        UiKit.Label(_gmRoot, "GM · 调试", 740, 268, 300, 46, 30, UiKit.Gold);
        UiKit.Button(_gmRoot, "关闭", 1080, 270, 100, 40, CloseGm);

        UiKit.Label(_gmRoot, "资源", 740, 336, 200, 34, 24, UiKit.Jade);
        var grant = UiKit.Button(_gmRoot, "每种货币 +10000", 740, 374, 290, 44, () => { _game.GrantAllCurrencies(); Refresh(); });
        grant.TooltipText = "item.csv 中每种货币各 +10000，新增货币自动纳入。";
        // 系统是逐个解锁的（开局全锁），开发时要看某一页不该先打一遍教学。
        var unlock = UiKit.Button(_gmRoot, "一键解锁", 1040, 374, 140, 44, () => { _game.UnlockAllSystems(); Refresh(); });
        unlock.TooltipText = "解锁全部系统（修行 / 境界 / 铸造 / 参悟 / 剑灵），与正常玩法走的是同一个解锁集合。";

        // 伤害统计是独立面板：先关掉自己再开，保证同时只有一个弹层（`Tap("关闭")` 是全树取首个匹配）。
        var damage = UiKit.Button(_gmRoot, "伤害统计", 740, 426, 145, 44, () => { CloseGm(); OpenDamage(); });
        damage.TooltipText = "按技能看累计伤害 / 每秒伤害，用来核对数值平衡。";
        // 修行星图的节点编辑器（开发期工具）。它**只写 TalentLayout.csv**，内容表仍手工维护；
        // 保存前按与加载期同一套规则自查，不合格就拒绝保存、不碰任何文件。
        var editor = UiKit.Button(_gmRoot, "节点编辑器", 895, 426, 145, 44, () => { CloseGm(); ToggleTalentEditor(); });
        editor.TooltipText = "编辑修行星图的格子与前置连线。只写布局表；新建节点会给内容表补一行骨架。";
        // 技能预览**从主界面搬进 GM**：它原来占着中间那条横带的一个位置，而那条横带整条撤掉了。
        // 按钮文字跟着状态走，进这里也能退出来。
        _previewToggle = UiKit.Button(_gmRoot, "", 1050, 426, 145, 44, () => { CloseGm(); TogglePreview(); });
        _previewToggle.TooltipText = "用独立会话逐个播放 15 个法术，不写存档；预览期间主线挂机暂停。再点一次退出。";

        // 怪物倍率：乘在关卡 / 波次倍率之后，**改完立刻作用于场上的怪**（不必等下一波）。
        // 按钮文字带上名字（"血量−" / "血量＋"），否则烟雾测试里 `Tap("＋")` 会撞上另外两行——
        // 它取的是全树第一个前缀匹配。
        UiKit.Label(_gmRoot, "怪物倍率", 740, 496, 200, 34, 24, UiKit.Jade);
        UiKit.Button(_gmRoot, "血量−", 740, 534, 100, 44, () => { _game.MonsterHpScale = StepScale(_game.MonsterHpScale, -1); RefreshGm(); })
            .TooltipText = "怪物血量的额外倍率（1 = 不额外缩放）。乘在关卡与波次倍率之后。";
        _monsterHpValue = UiKit.Label(_gmRoot, "", 850, 534, 260, 44, 24);
        UiKit.Button(_gmRoot, "血量＋", 1120, 534, 100, 44, () => { _game.MonsterHpScale = StepScale(_game.MonsterHpScale, 1); RefreshGm(); })
            .TooltipText = "怪物血量的额外倍率（1 = 不额外缩放）。改完立刻作用于场上的怪，不会等下一波。";
        UiKit.Button(_gmRoot, "攻击−", 740, 582, 100, 44, () => { _game.MonsterAtkScale = StepScale(_game.MonsterAtkScale, -1); RefreshGm(); })
            .TooltipText = "怪物攻击的额外倍率（1 = 不额外缩放）。";
        _monsterAtkValue = UiKit.Label(_gmRoot, "", 850, 582, 260, 44, 24);
        UiKit.Button(_gmRoot, "攻击＋", 1120, 582, 100, 44, () => { _game.MonsterAtkScale = StepScale(_game.MonsterAtkScale, 1); RefreshGm(); })
            .TooltipText = "怪物攻击的额外倍率（1 = 不额外缩放）。改完立刻作用于场上的怪。";

        // 主角无敌：**不掉血、不死亡**。死亡会清增益 + 重置冷却，于是伤害统计里各技能的周期会断掉、
        // 每秒数字失真——想量准就得先站得住。
        UiKit.Label(_gmRoot, "主角无敌", 740, 646, 200, 34, 24, UiKit.Jade);
        _invincibleButton = UiKit.Button(_gmRoot, InvincibleText, 1040, 640, 180, 44, () =>
        {
            _game.PlayerInvincible = !_game.PlayerInvincible;
            _invincibleButton.Text = InvincibleText;
            RefreshGm();
        });
        _invincibleButton.TooltipText = "不掉血、不死亡。死亡会清增益并重置冷却，让伤害统计断档——挂一局量数字前先打开它。";

        // 普攻形态：翻转修行树上的「剑气」解锁，用来对照近战与远程两种形态。
        // **它改的就是玩法状态本身**（不是另开一个会话开关）：所以两边行为一定一致，没有"调试态与真实态不同"的坑。
        UiKit.Label(_gmRoot, "普攻形态", 740, 692, 200, 34, 24, UiKit.Jade);
        _meleeButton = UiKit.Button(_gmRoot, MeleeText, 1040, 686, 180, 44, () =>
        {
            _game.DebugToggleRangedBasic();
            RefreshGm();
        });
        _meleeButton.TooltipText = "翻转「剑气」节点：近战 = 射程 150 / 停步 120 / 挥剑斩击；远程 = 射程 950 / 停步 640 / 平射飞剑。";

        // 波次数量：只影响**之后**刷出的波次与前行的下一格，已经在场的怪不动。
        UiKit.Label(_gmRoot, "波次数量", 740, 756, 200, 34, 24, UiKit.Jade);
        UiKit.Button(_gmRoot, "波次−", 740, 794, 100, 44, () => { _game.WaveBonus--; RefreshGm(); })
            .TooltipText = "每波额外少刷几只（下限 0）。";
        _waveBonusValue = UiKit.Label(_gmRoot, "", 850, 794, 260, 44, 24);
        UiKit.Button(_gmRoot, "波次＋", 1120, 794, 100, 44, () => { _game.WaveBonus++; RefreshGm(); })
            .TooltipText = "每波额外多刷几只，种类从普通怪里随机挑。上限 20。";
        // 分成两条定长标签而不是一条 `Wrapped`：CJK 长句在 `WordSmart` 下不一定断行，会直接溢出面板。
        UiKit.Label(_gmRoot, "倍率立刻作用于场上的怪；波次只影响之后刷出的波次。", 740, 852, 440, 26, 18, UiKit.Muted);
        UiKit.Label(_gmRoot, "开关都不写配置、不落盘。", 740, 878, 440, 26, 18, UiKit.Muted);
    }

    private void RefreshGm()
    {
        if (_gmRoot is null) return;
        _invincibleButton.Text = InvincibleText;
        _meleeButton.Text = MeleeText;
        if (_previewToggle is not null) _previewToggle.Text = _preview is null ? "技能预览" : "退出预览";
        _monsterHpValue.Text = $"×{_game.MonsterHpScale:0.##}";
        _monsterAtkValue.Text = $"×{_game.MonsterAtkScale:0.##}";
        _waveBonusValue.Text = $"+{_game.WaveBonus} 只";
    }
}

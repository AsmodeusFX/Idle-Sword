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
    private string InvincibleText => _game.PlayerInvincible ? "无敌：开" : "无敌：关";
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
        UiKit.PanelAt(_gmRoot, 700, 250, 520, 610);
        UiKit.Label(_gmRoot, "GM · 调试", 740, 268, 300, 46, 30, UiKit.Gold);
        UiKit.Button(_gmRoot, "关闭", 1080, 270, 100, 40, CloseGm);

        UiKit.Label(_gmRoot, "资源", 740, 336, 200, 34, 24, UiKit.Jade);
        var grant = UiKit.Button(_gmRoot, "每种货币 +10000", 740, 374, 440, 44, () => { _game.GrantAllCurrencies(); Refresh(); });
        grant.TooltipText = "item.csv 中每种货币各 +10000，新增货币自动纳入。";

        // 伤害统计是独立面板：先关掉自己再开，保证同时只有一个弹层（`Tap("关闭")` 是全树取首个匹配）。
        var damage = UiKit.Button(_gmRoot, "伤害统计", 740, 426, 440, 44, () => { CloseGm(); OpenDamage(); });
        damage.TooltipText = "按技能看累计伤害 / 每秒伤害，用来核对数值平衡。";

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

        // 波次数量：只影响**之后**刷出的波次与前行的下一格，已经在场的怪不动。
        UiKit.Label(_gmRoot, "波次数量", 740, 700, 200, 34, 24, UiKit.Jade);
        UiKit.Button(_gmRoot, "波次−", 740, 738, 100, 44, () => { _game.WaveBonus--; RefreshGm(); })
            .TooltipText = "每波额外少刷几只（下限 0）。";
        _waveBonusValue = UiKit.Label(_gmRoot, "", 850, 738, 260, 44, 24);
        UiKit.Button(_gmRoot, "波次＋", 1120, 738, 100, 44, () => { _game.WaveBonus++; RefreshGm(); })
            .TooltipText = "每波额外多刷几只，种类从普通怪里随机挑。上限 20。";
        // 分成两条定长标签而不是一条 `Wrapped`：CJK 长句在 `WordSmart` 下不一定断行，会直接溢出面板。
        UiKit.Label(_gmRoot, "倍率立刻作用于场上的怪；波次只影响之后刷出的波次。", 740, 796, 440, 26, 18, UiKit.Muted);
        UiKit.Label(_gmRoot, "开关都不写配置、不落盘。", 740, 822, 440, 26, 18, UiKit.Muted);
    }

    private void RefreshGm()
    {
        if (_gmRoot is null) return;
        _invincibleButton.Text = InvincibleText;
        _monsterHpValue.Text = $"×{_game.MonsterHpScale:0.##}";
        _monsterAtkValue.Text = $"×{_game.MonsterAtkScale:0.##}";
        _waveBonusValue.Text = $"+{_game.WaveBonus} 只";
    }
}

using Godot;

namespace IdleSword.UI;

/// <summary>
/// 序章：开场演出。土豆从天上落下来，一路向右走，走到画面右半边渐黑，切第一关。
/// 土豆**开局就背着剑**，没有捡剑动画（用户明确简化过）。
///
/// **它不推进 <see cref="GameSession"/>。** 构造函数已经把第一关摆成原始状态（PlayerX=80、满血、
/// 无刷怪无敌人），只要不 Step 它就一直是干净的——所以过场期间没有刷怪、没有战斗、也不写存档，
/// 是**构造上没有**，不是靠抑制。走路与镜头全在表现层：玩家在屏幕上的位置走
/// <see cref="BattleView.PlayerScreenX"/>，镜头冻在 <see cref="BattleView.CameraOverride"/> 上
/// （那正是"镜头不跟随"）。
///
/// 时钟走 Main 那条固定步累加器（与模拟同频），**不引入 tween**——全项目都没有 tween。
/// </summary>
public partial class Main
{
    // 节奏常量：改时长/落点只动这一组。
    private const double DropSeconds = .55, LandSeconds = .30, WalkSeconds = 2.30,
                         FadeOutSeconds = .45, WalkInSeconds = .55, FadeInSeconds = .25;
    private const float DropHeight = 520;       // 起落高度。比战斗区（400 高）大，所以一开始在画面上方之外
    private const float LandScreenX = 900;      // 落点：画面中间偏左，留出向右走的余地
    private const float WalkToScreenX = 1420;   // 走到这里渐黑（画面右半边）
    private const float WalkInLead = 160;       // 交接时镜头拖后这么多，读成"土豆从左边走进来"
    // 过场里战斗区的位置与高度。HUD 收起来后不下移的话画面会挤在上半屏；
    // 高度必须给到 440（常态是 400），否则底部那条地面带会被 ClipContents 切出一条明显的横缝。
    private const float PrologueBattleY = 340;
    private static readonly Vector2 PrologueBattleSize = new(1920, 440);

    private enum ProloguePhase { Drop, Land, Walk, FadeOut, WalkIn }

    private sealed class PrologueState
    {
        public ProloguePhase Phase = ProloguePhase.Drop;
        public double Clock;        // 当前阶段已过的秒数
        public bool Entered;        // 本阶段"进入时只跑一次"的那部分是否跑过
        public bool ShotTaken;      // 本阶段的留图是否已经拍过
    }

    /// <summary>开始序章。只在全新存档、或 --prologue 强制时调用。</summary>
    private void StartPrologue()
    {
        if (_prologue is not null) return;
        _prologue = new PrologueState();
        // 镜头冻在玩家当前的世界位置：世界不再跟着玩家平移，而玩家自己在**屏幕坐标**上走。
        _battle.CameraOverride = (float)_game.Battle.PlayerX;
        _battle.PlayerScreenX = LandScreenX;
        _battle.PlayerAirHeight = DropHeight;
        _battle.MovingOverride = false;
        SetHudVisible(false);
        _battle.Position = new Vector2(_battleHome.X, PrologueBattleY);   // 画面下移到接近居中
        _battle.Size = PrologueBattleSize;
        _fade.MoveToFront();     // 遮罩要压过一切——包括以后才 AddChild 的那些面板
        GD.Print("PROLOGUE PLAY");
    }

    /// <summary>推进序章。由 <c>Main._Process</c> 的固定步循环调用——此时**不**推进会话。</summary>
    private void TickPrologue(double step)
    {
        var p = _prologue!;
        p.Clock += step;
        if (!p.Entered) { p.Entered = true; EnterPhase(p); }

        switch (p.Phase)
        {
            case ProloguePhase.Drop:
            {
                double u = Math.Clamp(p.Clock / DropSeconds, 0, 1);
                _battle.PlayerAirHeight = (float)(DropHeight * (1 - u * u));   // 二次曲线 = 加速下落
                if (p.Clock >= DropSeconds) Next(p, ProloguePhase.Land);
                break;
            }
            case ProloguePhase.Land:
                if (p.Clock >= LandSeconds) Next(p, ProloguePhase.Walk);
                break;
            case ProloguePhase.Walk:
            {
                double u = Math.Clamp(p.Clock / WalkSeconds, 0, 1);
                _battle.PlayerScreenX = (float)(LandScreenX + (WalkToScreenX - LandScreenX) * u);
                if (p.Clock >= WalkSeconds) Next(p, ProloguePhase.FadeOut);
                break;
            }
            case ProloguePhase.FadeOut:
                _fade.Color = new Color(0, 0, 0, (float)Math.Clamp(p.Clock / FadeOutSeconds, 0, 1));
                if (p.Clock >= FadeOutSeconds) Next(p, ProloguePhase.WalkIn);
                break;
            case ProloguePhase.WalkIn:
                // 交接之后关卡是活的：从这一步起真的推进会话（刷怪恢复）。敌人要到世界坐标 ~1490 才出现，
                // 而这段里玩家只走到 ~270，所以走完入场还碰不到任何东西。
                _game.Step(step);
                double v = Math.Clamp(p.Clock / WalkInSeconds, 0, 1);
                double ease = 1 - Math.Pow(1 - v, 3);
                _battle.CameraOverride = (float)(_game.Battle.PlayerX + WalkInLead * (1 - ease));
                _fade.Color = new Color(0, 0, 0, (float)Math.Clamp(1 - p.Clock / FadeInSeconds, 0, 1));
                if (p.Clock >= WalkInSeconds) FinishPrologue();
                break;
        }

        // 留图。每段取一个"最能说明问题"的时刻，而不是一律进入时——走路那段尤其要在半路上拍，
        // 否则只能拍到起点，看不出它在动。
        if (!p.ShotTaken && p.Clock >= ShotAt(p.Phase))
        {
            p.ShotTaken = true;
            _ = CaptureFrame(PhaseShot(p.Phase));
        }
    }

    /// <summary>进入某阶段时**只跑一次**的那部分：触发落地反馈、切换步伐、还原覆盖量。</summary>
    private void EnterPhase(PrologueState p)
    {
        switch (p.Phase)
        {
            case ProloguePhase.Land:
                _battle.PlayerAirHeight = 0;
                _battle.PlayLandingSquash();
                _battle.SpawnLandingDust();
                break;
            case ProloguePhase.Walk:
                _battle.MovingOverride = true;      // 步伐 bob / 挤压 / 扬尘全靠它
                break;
            case ProloguePhase.FadeOut:
                _battle.MovingOverride = false;
                break;
            case ProloguePhase.WalkIn:
                // 换场：把四个覆盖量还原成默认值。第一关从头到尾没被推进过，所以**不需要任何清理**。
                _battle.CameraOverride = null;
                _battle.PlayerScreenX = BattleView.PlayerAnchor;
                _battle.PlayerAirHeight = 0;
                _battle.MovingOverride = null;
                _battle.ResetTransient();       // 清掉残余扬尘
                _fade.Color = new Color(0, 0, 0, 1);
                _fade.MoveToFront();
                SetHudVisible(true);
                // 布局归位。此时画面还全黑，所以这一下看不出来。
                _battle.Position = _battleHome;
                _battle.Size = _battleHomeSize;
                break;
        }
    }

    private void FinishPrologue()
    {
        // 位移已经归零，把镜头交还给玩家不会有任何跳变。
        _battle.CameraOverride = null;
        _fade.Color = new Color(0, 0, 0, 0);
        _prologue = null;
        _prologuePlayed = true;
        _frames = 0;    // 冒烟的 100 帧预热从交接之后重新数，见 _Process 里的计数门
        GD.Print("PROLOGUE DONE");
    }

    private static void Next(PrologueState p, ProloguePhase next)
    {
        p.Phase = next; p.Clock = 0; p.Entered = false; p.ShotTaken = false;
    }

    /// <summary>每段在哪一刻留图。取的是"这一段最有信息量的瞬间"。</summary>
    private static double ShotAt(ProloguePhase phase) => phase switch
    {
        ProloguePhase.Drop => DropSeconds * .72,      // 已经落进画面、还在下坠
        ProloguePhase.Land => 0,                      // 触地那一刻，挤压最重
        ProloguePhase.Walk => WalkSeconds * .5,       // 走到半路，看得出在移动
        ProloguePhase.FadeOut => FadeOutSeconds * .8, // 快黑透
        _ => FadeInSeconds,                           // 入场：渐亮刚完成
    };

    private static string PhaseShot(ProloguePhase phase) => phase switch
    {
        ProloguePhase.Drop => "-prologue-drop",
        ProloguePhase.Land => "-prologue-land",
        ProloguePhase.Walk => "-prologue-walk",
        ProloguePhase.FadeOut => "-prologue-fade",
        _ => "-prologue-walkin",
    };

    /// <summary>过场期间收起 HUD（标题除外）。节点清单见 <c>_hudNodes</c>。</summary>
    private void SetHudVisible(bool visible)
    {
        _hudHidden = !visible;
        foreach (var node in _hudNodes) node.Visible = visible;
        // 星图与锁那三层、以及战斗区里那条「点击鼠标攻击敌人」气泡，都不在 `_hudNodes` 里（理由见各自的说明），
        // 它们各自有自己的开关——**统一交给这两个方法**，免得"过场结束全点亮"把锁着的星图一起露出来。
        RefreshHudLayers();
        _battle.ShowClickHint = visible;
    }
}

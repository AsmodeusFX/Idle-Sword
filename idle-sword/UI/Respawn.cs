using Godot;
using IdleSword.Features;

namespace IdleSword.UI;

/// <summary>
/// 阵亡与复活的表演：土豆**向后倒地** → 画面压暗 → 镜头切回关卡起点 → 画面变亮的同时**从天上落下**。
///
/// 与 [Prologue.cs] 同一套路——**镜头与全屏遮罩都归 Main**，所以也挂在这里。
/// **它不改任何玩法状态**：倒地、变暗、切镜头、落下全靠 <see cref="BattleView"/> 上那几个覆盖量；
/// 整关的重置是 `GameSession` 在复活读条归零那一刻做的（见 `Die` 的说明）。
/// </summary>
public partial class Main
{
    // 四拍的分界写成**占读条总长的比例**，所以 respawn_seconds 改了会自动跟着缩放。
    // 当前 respawn_seconds = 2，于是四拍约是 0.70 / 0.30 / 0.76 / 0.24 秒。
    private const float DeathFallEnd = .26f, DeathCutEnd = .40f, DeathLandEnd = .88f;
    private const float DeathDim = .55f;      // 画面压到多暗
    private const float DeathTilt = -1.45f;   // 倒地角；负 = 向后（朝屏幕左侧）倒

    private bool _deathActive, _deathCut, _deathLanded;

    /// <summary>按**渲染时钟**推进（不是固定步）：表演归表现层，不该被模拟暂停影响。</summary>
    private void TrackDeath(double delta)
    {
        if (_prologue is not null || _game is null) return;
        double timer = _game.Battle.RespawnTimer;
        // 按 **0 → 正** 的跳变触发。读档进来时倒计时可能已经 > 0，那种情况不播——
        // 那一局根本没有"死亡点"可言，硬播反而像凭空倒地。
        if (timer > 0) { if (!_deathActive) StartDeath(); TickDeath(delta, timer); }
        else if (_deathActive) FinishDeath();
    }

    private void StartDeath()
    {
        _deathActive = true; _deathCut = false; _deathLanded = false;
        // `Die()` 不重置关卡，所以此刻 PlayerX **就是死亡点**。把镜头冻在它上面：背景一动不动，
        // 土豆在原地倒下——而不是"人刚倒、世界已经换了一张"。
        _battle.CameraOverride = (float)_game.Battle.PlayerX;
        // 场上的怪**就地转成尸体**：跟着压暗一起爆开渐隐。
        // 真正的清场要等 2 秒后读条归零（那是模拟层的事），那时画面早亮回来了——
        // 让怪在那一刻凭空消失才是突兀的那一下，所以尸体必须在这时候就落。
        _battle.BeginDeathWipe();
        _fade.MoveToFront();
    }

    private void TickDeath(double delta, double timer)
    {
        _ = delta;
        double total = _game.Config.Setting("respawn_seconds");
        float u = total > 0 ? (float)Math.Clamp(1 - timer / total, 0, 1) : 1f;

        if (u < DeathFallEnd)
        {
            // ① 倒地：绕脚底向后倒，同时把画面压暗。
            // **缓出**而不是匀速：前段"啪"地甩过去、后段收住，读起来才利索。
            float k = u / DeathFallEnd;
            float ease = 1 - (1 - k) * (1 - k);
            _battle.PlayerTilt = DeathTilt * ease;
            _fade.Color = new Color(0, 0, 0, DeathDim * ease);
        }
        else if (u < DeathCutEnd)
        {
            // ② 切场：保持倒姿与全暗。**只做一次**——把镜头放开、人先离场，这一下被暗场挡着。
            _battle.PlayerTilt = DeathTilt;
            _fade.Color = new Color(0, 0, 0, DeathDim);
            if (!_deathCut)
            {
                _deathCut = true;
                // 镜头挪到关卡起点：等读条归零 `EnterLevel` 真跑起来时 PlayerX 已经是这个值，
                // 那时放开覆盖不会有任何跳变。高度顶到天上 = 人先离场，等第 ③ 拍再落下来。
                _battle.CameraOverride = (float)GameSession.LevelStartX;
                _battle.PlayerAirHeight = DropHeight;
            }
        }
        else if (u < DeathLandEnd)
        {
            // ③ 落下：**变亮与下落同时**。下落时人已经是站姿，否则会像一块板子拍下来。
            float k = (u - DeathCutEnd) / (DeathLandEnd - DeathCutEnd);
            _battle.PlayerTilt = 0;
            _battle.PlayerAirHeight = (float)(DropHeight * (1 - k * k));
            _fade.Color = new Color(0, 0, 0, DeathDim * (1 - k));
        }
        else
        {
            // ④ 落地：压扁 + 一圈扬尘（序章那两个现成的）。同样只触发一次。
            _battle.PlayerAirHeight = 0; _battle.PlayerTilt = 0;
            _fade.Color = new Color(0, 0, 0, 0);
            if (!_deathLanded)
            {
                _deathLanded = true;
                _battle.PlayLandingSquash();
                _battle.SpawnLandingDust();
            }
        }
    }

    /// <summary>读条走完，把四个覆盖量与遮罩全部收干净——之后就是正常玩法在跑。</summary>
    private void FinishDeath()
    {
        _deathActive = false;
        // 放开"别画活着的敌人"。这时 `EnterLevel` 已经真的把那批怪移走了（它在 Step 里跑的，
        // 而 Main 是先跑固定步、再调 TrackDeath），所以放开隐藏不会让它们闪回来。
        _battle.EndDeathWipe();
        _battle.CameraOverride = null;
        _battle.PlayerAirHeight = 0;
        _battle.PlayerTilt = 0;
        _fade.Color = new Color(0, 0, 0, 0);
    }
}

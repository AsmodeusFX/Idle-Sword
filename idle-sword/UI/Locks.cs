using Godot;

namespace IdleSword.UI;

/// <summary>
/// 系统解锁的「锁」：页签角上的小锁、操作区上的遮罩与锁，以及**解锁那一刻的炸开**。
///
/// 为什么要有"解锁那一刻"（用户要求）：系统是逐个开的，锁若只是"下一帧就不见了"，
/// 玩家根本不会注意到刚才发生了什么。炸开把这一下变成**有始有终的事件**——读法与怪物死亡的
/// 那一套一致（白光核 + 扩散的环 + 几块四散的碎片），所以它读成"碎了"，而不是"消失了"。
///
/// 纯程序化绘制，不引入素材；颜色都从 `UiKit` 取，跟着主题走。
/// </summary>
public static class Locks
{
    /// <summary>一次炸开演多久（秒）。</summary>
    public const double BurstLife = .55;

    /// <summary>
    /// 画一把挂锁：锁体在下、锁梁在上。**必须有锁孔**——一把实心方块读不出是锁，
    /// 只会被当成"这里有个黄点"。
    /// </summary>
    public static void Padlock(CanvasItem canvas, Vector2 center, float size, Color color, Color hole)
    {
        float bodyW = size * .78f, bodyH = size * .6f;
        var body = new Rect2(center.X - bodyW / 2, center.Y - bodyH / 2 + size * .18f, bodyW, bodyH);
        canvas.DrawRect(body, color);
        // 锁梁：以锁体上沿为圆心画上半圈。
        canvas.DrawArc(new Vector2(center.X, body.Position.Y), size * .27f,
            Mathf.Pi, Mathf.Tau, 14, color, Mathf.Max(2f, size * .13f));
        // 锁孔：上圆下缝。
        float holeR = size * .1f;
        canvas.DrawCircle(new Vector2(center.X, body.Position.Y + bodyH * .42f), holeR, hole);
        canvas.DrawRect(new Rect2(center.X - holeR * .34f, body.Position.Y + bodyH * .42f, holeR * .68f, bodyH * .3f), hole);
    }

    /// <summary>一次炸开的进度状态。位置用**全局坐标**，所以画它的那一层可以铺满整屏。</summary>
    public sealed class Burst
    {
        public Vector2 At;
        public double Life, MaxLife;
        public float Size;
    }

    /// <summary>画一次炸开：白光核 + 扩散的环 + 八块四散的碎片，越飞越淡。</summary>
    public static void DrawBurst(CanvasItem canvas, Burst burst, Color color)
    {
        float t = (float)(1 - burst.Life / Math.Max(1e-6, burst.MaxLife));
        float fade = 1 - t;
        canvas.DrawCircle(burst.At, burst.Size * (.2f + t * .45f), new Color(1, 1, 1, .5f * fade));
        canvas.DrawArc(burst.At, burst.Size * (.3f + t * .95f), 0, Mathf.Tau, 26, new Color(color, .75f * fade), 3);
        // 碎片是关键：只画光圈的话读成"闪了一下"，飞出几块才读成"碎了"。
        for (int i = 0; i < 8; i++)
        {
            float a = i * Mathf.Tau / 8 + burst.Size * .07f;
            var p = burst.At + new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * burst.Size * (.28f + t * 1.15f);
            canvas.DrawRect(new Rect2(p.X - 3, p.Y - 3, 6, 6), new Color(color, .85f * fade));
        }
    }
}

/// <summary>页签角上的小锁。**不拦点击**：页签按钮自己已经置灰了，这里只是让"锁着"一眼看得见。</summary>
public partial class LockBadge : Control
{
    // **必须自己 QueueRedraw**：Godot 只在节点第一次绘制与显式请求时跑 `_Draw`，
    // 改一个普通属性不会重画——锁会停在"创建时那一帧"的样子（要么一直挂着，要么一直不出现）。
    private bool _locked;
    public bool Locked
    {
        get => _locked;
        set { if (_locked == value) return; _locked = value; QueueRedraw(); }
    }

    public LockBadge() => MouseFilter = MouseFilterEnum.Ignore;

    public override void _Ready() => QueueRedraw();

    public override void _Draw()
    {
        if (!_locked) return;
        Locks.Padlock(this, Size / 2, Mathf.Min(Size.X, Size.Y), UiKit.Gold, new Color("#0b1620"));
    }
}

/// <summary>
/// 未解锁的系统在操作区上压的那层：压暗 + 一把锁，并**拦住点击**（用户要求"不允许玩家点击"）。
/// 锁自己轻微呼吸——一块纹丝不动的遮罩容易被读成"页面坏了"。
/// </summary>
public partial class LockMask : Control
{
    // **和 `LockBadge` 是同一个坑，这里也必须有**：Godot 只在节点第一次绘制与显式请求时跑 `_Draw`，
    // 改一个普通属性不会重画。漏掉 `QueueRedraw` 的后果不是"锁不出现"，而是**锁一直挂着**——
    // 解锁之后 `_Process` 因为 `!Locked` 提前返回、再没人请求重画，上一帧那块遮罩就永远留在屏幕上。
    private bool _locked;
    public bool Locked
    {
        get => _locked;
        set { if (_locked == value) return; _locked = value; QueueRedraw(); }
    }
    private double _clock;

    public override void _Process(double delta)
    {
        if (!_locked) return;
        _clock += delta;
        QueueRedraw();
    }

    public override void _Draw()
    {
        if (!_locked) return;
        DrawRect(new Rect2(Vector2.Zero, Size), new Color(0, 0, 0, .45f));
        float pulse = 1 + .05f * (float)Math.Sin(_clock * 2.4);
        Locks.Padlock(this, Size / 2, 74 * pulse, UiKit.Gold, new Color("#0b1620"));
    }
}

/// <summary>
/// 解锁炸开的动画层：铺满整屏、**不拦点击**，画在最上层。
/// 位置用全局坐标，所以调用方直接把页签/遮罩的 `GetGlobalRect().GetCenter()` 丢进来即可。
/// </summary>
public partial class LockFx : Control
{
    private readonly List<Locks.Burst> _bursts = [];

    public LockFx() => MouseFilter = MouseFilterEnum.Ignore;

    public void Play(Vector2 at, float size) =>
        _bursts.Add(new() { At = at, Life = Locks.BurstLife, MaxLife = Locks.BurstLife, Size = size });

    public override void _Process(double delta)
    {
        if (_bursts.Count == 0) return;
        foreach (var burst in _bursts) burst.Life -= delta;
        _bursts.RemoveAll(b => b.Life <= 0);
        QueueRedraw();
    }

    public override void _Draw()
    {
        foreach (var burst in _bursts) Locks.DrawBurst(this, burst, UiKit.Gold);
    }
}

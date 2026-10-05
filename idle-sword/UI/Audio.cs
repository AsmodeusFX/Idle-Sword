using Godot;
using IdleSword.Core;
using IdleSword.Features;

namespace IdleSword.UI;

/// <summary>
/// 按钮点击音的极薄转发。UiKit 是静态工具类、拿不到 Main 实例，用一个静态入口让全游戏按钮
/// 都能发声，不必在每个创建点单独接线。Main 在 _Ready 注册、_ExitTree 注销——
/// 静态字段若一直指着已释放的节点，之后任何按钮点击都会抛 ObjectDisposedException。
/// </summary>
public static class Sfx
{
    public static Action<string>? Play;
    public static void Click() => Play?.Invoke("sfx_click");
}

/// <summary>
/// 音频播放。素材是程序化生成的 8bit 占位音，映射见 Assets/audio.json。
/// 音频完全属于表现层：触发点全部在 UI 侧从公开状态推导，不往 Core/ 回写任何仅供显示的状态。
/// </summary>
public partial class Main
{
    // 8 个播放器轮转复用，让密集命中互相叠加，而不是彼此打断。
    private const int SfxVoices = 8;
    private const string SfxBus = "SFX", MusicBus = "Music";

    private readonly Dictionary<string, AudioStream> _audio = [];
    private readonly Dictionary<string, int> _sfxCounts = [];
    private AudioStreamPlayer[] _sfxPlayers = [];
    private AudioStreamPlayer? _music;
    private int _sfxCursor, _sfxPlays;
    private string? _audioLoadError;

    /// <summary>
    /// 单个音效的重复抑制策略。战斗里同一个音效可能在一秒内被触发几十次（持续伤害逐跳结算、
    /// 范围技能下同一帧打中多个目标、密集小怪同时挨打），直接播会变成机关枪，既刺耳也丢信息。
    /// 行业常规做法是三道闸门：最短重触发间隔（去抖）、同音并发上限、音高微随机。
    /// 这里取"最短间隔 + 音高抖动"两条，按 id 配置；未列出的 id 用宽松默认值。
    /// </summary>
    private sealed record SfxPolicy(double MinGap, double Jitter);
    private static readonly SfxPolicy DefaultSfxPolicy = new(.03, 0);
    private static readonly Dictionary<string, SfxPolicy> SfxPolicies = new()
    {
        // 命中类给得最紧：它们是唯一会被"每个敌人每次挨打"触发的音效。
        ["sfx_hit"] = new(.09, .10),
        ["sfx_hit_heavy"] = new(.14, .06),
        ["sfx_kill"] = new(.10, .10),
    };
    // 记录各音效上次真正播放的模拟时刻，以及被抑制的累计次数（供自检读取）。
    private readonly Dictionary<string, double> _sfxLastAt = [];
    private readonly Random _sfxJitter = new();
    private int _sfxThrottled;

    /// <summary>
    /// 读取映射并加载素材。整体 try/catch：音频是可选素材，缺失时游戏照常跑、只是没声音，
    /// 不能因此让启动失败——尤其不能在 _Ready 里靠抛异常报错（Godot 会吞掉子节点的异常，
    /// 结果是"没声音但自检照样通过"）。缺失一律记进 AudioLoadError，由自检断言。
    /// </summary>
    private void LoadAudio()
    {
        try
        {
            var manifest = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(
                Godot.FileAccess.GetFileAsString("res://Assets/audio.json"));
            if (manifest is null || manifest.Count == 0) _audioLoadError = "音频资源映射为空";
            else
            {
                var missing = new List<string>();
                foreach (var (id, path) in manifest)
                {
                    var stream = GD.Load<AudioStream>(path);
                    if (stream is null) missing.Add(path); else _audio[id] = stream;
                }
                if (missing.Count > 0) _audioLoadError = "缺少音频资源（未导入或路径错误）: " + string.Join(", ", missing);
            }
        }
        catch (Exception e) when (e is System.Text.Json.JsonException or IOException or ArgumentException)
        {
            _audioLoadError = "音频资源映射读取失败：" + e.Message;
        }
        if (_audioLoadError is not null) { _audio.Clear(); return; }

        // 播放器必须在总线已存在之后创建：ApplyAudio() 负责建 Music/SFX 两条总线，
        // 顺序颠倒的话 Bus 名会落到不存在的总线上，静默回退到 Master。
        _sfxPlayers = new AudioStreamPlayer[SfxVoices];
        for (int i = 0; i < SfxVoices; i++)
        {
            _sfxPlayers[i] = new AudioStreamPlayer { Bus = SfxBus };
            AddChild(_sfxPlayers[i]);
        }
        _music = new AudioStreamPlayer { Bus = MusicBus };
        AddChild(_music);
        ApplyLoop("bgm_battle");
    }

    /// <summary>
    /// 循环必须在代码里设满三项。WAV 导入器默认 loop_mode=0（不从文件探测循环点），
    /// 而运行时 loop_end 的默认值是 0——那不是"到文件末尾"，是空循环区间：
    /// 只设 LoopMode 会看着对、实际不循环。GD.Load 按路径缓存，每次加载后都要重设。
    /// </summary>
    private void ApplyLoop(string id)
    {
        if (!_audio.TryGetValue(id, out var stream) || stream is not AudioStreamWav wav) return;
        int bytesPerFrame = wav.Format switch
        {
            AudioStreamWav.FormatEnum.Format8Bits => 1,
            AudioStreamWav.FormatEnum.Format16Bits => 2,
            _ => 0,   // 压缩格式的样本数不能按字节数推算
        };
        if (bytesPerFrame == 0 || wav.Data.Length == 0) return;
        wav.LoopMode = AudioStreamWav.LoopModeEnum.Forward;
        wav.LoopBegin = 0;
        wav.LoopEnd = wav.Data.Length / (bytesPerFrame * (wav.Stereo ? 2 : 1));
        GD.Print($"AUDIO {id} format={wav.Format} mix_rate={wav.MixRate} frames={wav.LoopEnd}");
    }

    private void PlaySfx(string id)
    {
        if (_audioLoadError is not null || _sfxPlayers.Length == 0) return;
        if (!_audio.TryGetValue(id, out var stream)) return;
        var policy = SfxPolicies.GetValueOrDefault(id, DefaultSfxPolicy);
        // 用模拟时钟而不是真实时间：放置游戏的模拟时间会与真实时间脱节（追帧、预览加速、
        // 一次长帧合并多个 Step），声音跟着模拟事件走才不会把一秒内的事件全挤在一瞬间放出来。
        // 也让自检可以确定性地核对抑制行为。now < last 说明换了会话（时钟归零），此时放行。
        double now = Active.Elapsed;
        double last = _sfxLastAt.GetValueOrDefault(id, double.NegativeInfinity);
        if (now >= last && now - last < policy.MinGap) { _sfxThrottled++; return; }
        _sfxLastAt[id] = now;
        // 轮转而不是找空闲槽：headless 的 dummy 音频驱动下 Playing 的时序语义与真机不同，
        // 用播放状态维护池会卡死；轮转天然做到"最老的最先被抢占"，正是密集命中想要的行为。
        var player = _sfxPlayers[_sfxCursor];
        _sfxCursor = (_sfxCursor + 1) % _sfxPlayers.Length;
        // 音高微随机：连续重复同一个音会听出机械感，抖动几个百分点就自然得多。
        player.PitchScale = policy.Jitter <= 0 ? 1f : (float)(1 + (_sfxJitter.NextDouble() * 2 - 1) * policy.Jitter);
        player.Stream = stream;
        player.Play();
        _sfxPlays++;
        _sfxCounts[id] = _sfxCounts.GetValueOrDefault(id) + 1;
    }

    /// <summary>
    /// 启动 BGM。音量交给总线 dB 处理，不在这里按音量判断是否播放——
    /// 否则启动时音量为 0 就永远不会开始播，玩家把滑杆拉上来也不响，只能重启。
    /// </summary>
    private void StartBgm()
    {
        if (_audioLoadError is not null || _music is null) return;
        if (!_audio.TryGetValue("bgm_battle", out var stream)) return;
        _music.Stream = stream;
        _music.Play();
    }

    private int SfxCount(string id) => _sfxCounts.GetValueOrDefault(id);

    // ── 触发点：技能释放与首杀奖励 ────────────────────────────────────
    private readonly Dictionary<string, double> _lastCooldowns = [];
    private BattleState? _castBaseline;
    private int _firstKillsSeen = -1;

    /// <summary>
    /// 技能释放用"冷却 0 → 正"的跳变检测，而不是比较 Session.Effects 的引用集合：
    /// 3 个 buff 类法术走 CastBuff，根本不产生 CombatEffect，差集法会永久漏掉它们。
    /// CastSkills 在真正出手时必定写入 Cooldowns[id]（无目标会 continue，不写冷却），五种 kind 一律成立。
    /// 普攻（GameSession.BasicAttackKey）刻意不走这里：它每秒一发，再叠上御剑 1.2 秒一轮的出手音，
    /// 出路音会盖过背景音乐；它的反馈已经由命中音承担，不与五大 kind 的释放音混为一谈。
    /// </summary>
    private void TrackSkillCasts(GameSession session)
    {
        var battle = session.Battle;
        if (!ReferenceEquals(_castBaseline, battle))
        {
            // 换场或换会话只重建基线、不触发，否则读档时残留的冷却值会在开局误报一片释放音。
            _castBaseline = battle;
            _lastCooldowns.Clear();
            foreach (var id in session.Config.Skills.Keys) _lastCooldowns[id] = battle.Cooldowns.GetValueOrDefault(id);
            return;
        }
        foreach (var (id, skill) in session.Config.Skills)
        {
            double now = battle.Cooldowns.GetValueOrDefault(id);
            if (now > 0 && _lastCooldowns.GetValueOrDefault(id) <= 0) PlaySfx("sfx_cast_" + skill.Kind);
            _lastCooldowns[id] = now;
        }
    }

    /// <summary>首杀奖励音。用 FirstKills 计数增加触发，比差分可靠——它有明确状态。</summary>
    private void TrackFirstKills()
    {
        int now = _game.State.FirstKills.Count;
        // 重置进度会让它归零，所以只认增加；首次观察只建基线。
        if (_firstKillsSeen >= 0 && now > _firstKillsSeen) PlaySfx("sfx_reward");
        _firstKillsSeen = now;
    }
}

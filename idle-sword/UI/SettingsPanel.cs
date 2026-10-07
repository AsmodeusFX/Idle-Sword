using Godot;
using IdleSword.Core;
using IdleSword.Features;

namespace IdleSword.UI;

/// <summary>
/// 设置面板：画面（分辨率 / 全屏）、音频（音乐 / 音效音量）、重置进度。
/// 面板按需构建并置于最上层；设置存于独立文件，重置进度不会连带清掉画面与音量偏好。
/// </summary>
public partial class Main
{
    private static readonly (string Label, int Width, int Height)[] Resolutions =
    [
        ("1280 × 720", 1280, 720),
        ("1600 × 900", 1600, 900),
        ("1920 × 1080", 1920, 1080),
        ("2560 × 1440", 2560, 1440),
        ("3840 × 2160", 3840, 2160),
    ];
    private const double ResetConfirmSeconds = 5;
    // 拖动窗口停止后延迟这么久再写设置文件，避开拖动过程中的连续落盘。
    private const double DisplayPersistDelay = .6;

    private AppSettings _settings = new();
    private SettingsStore _settingsStore = null!;
    private Control? _settingsRoot;
    private OptionButton _resolutionBox = null!;
    private CheckButton _fullscreenBox = null!;
    private Label _resolutionLabel = null!;
    private HSlider _musicSlider = null!, _soundSlider = null!;
    private Label _musicValue = null!, _soundValue = null!;
    private Button _resetButton = null!;
    private readonly List<(string Label, int Width, int Height)> _resolutionOptions = [];
    // > 0 表示重置已进入二次确认，倒计时结束自动撤销，避免误点一下就清档。
    private double _resetArmed;
    // 拖动窗口边缘会连续触发 SizeChanged，尺寸稳定后才落盘，避免拖动过程每帧写文件。
    private bool _displayDirty;
    private double _displayPersistDelay;
    // 需要推迟一帧再套用的窗口尺寸，见 ApplyDisplay 的说明。
    private Vector2I? _pendingSize;

    private void ToggleSettings()
    {
        if (_settingsRoot is not null && _settingsRoot.Visible) CloseSettings();
        else OpenSettings();
    }
    private void OpenSettings()
    {
        if (_settingsRoot is null) BuildSettings();
        RefreshSettings();
        _settingsRoot!.Visible = true;
        PlaySfx("sfx_panel");
    }
    private void CloseSettings()
    {
        // 已经关着就什么都不做：否则重置流程里那次内部调用会多播一次开合音。
        if (_settingsRoot is null || !_settingsRoot.Visible) return;
        _settingsRoot.Visible = false;
        DisarmReset();
        PlaySfx("sfx_panel");
    }
    /// <summary>重置的二次确认倒计时；面板关闭时不再消耗。</summary>
    private void TickSettings(double delta)
    {
        if (_pendingSize is { } pending) { _pendingSize = null; ApplyWindowSize(pending); }
        if (_displayDirty)
        {
            _displayPersistDelay -= delta;
            if (_displayPersistDelay <= 0) { _displayDirty = false; PersistSettings(); }
        }
        if (_resetArmed <= 0) return;
        _resetArmed -= delta;
        if (_resetArmed <= 0) DisarmReset();
    }
    private void DisarmReset()
    {
        _resetArmed = 0;
        if (_resetButton is null) return;
        _resetButton.Text = "重置游戏进度";
        _resetButton.AddThemeColorOverride("font_color", DangerText);
    }

    private static readonly Color DangerBg = new("#4a2733"), DangerEdge = new("#b2564f"), DangerText = new("#e8a49b");

    private void BuildSettings()
    {
        _settingsRoot = new Control { Visible = false }; UiKit.Place(_settingsRoot, 0, 0, 1920, 1080); AddChild(_settingsRoot);
        // 背板拦截点击，避免点到面板后面的页签与按钮。
        var backdrop = new ColorRect { Color = new Color(0, 0, 0, .62f) }; UiKit.Place(backdrop, 0, 0, 1920, 1080); _settingsRoot.AddChild(backdrop);
        UiKit.PanelAt(_settingsRoot, 620, 180, 680, 580);
        UiKit.Label(_settingsRoot, "设置", 660, 198, 300, 46, 30, UiKit.Gold);
        UiKit.Button(_settingsRoot, "关闭", 1140, 200, 120, 40, CloseSettings);

        UiKit.Label(_settingsRoot, "画面", 660, 254, 200, 34, 24, UiKit.Jade);
        // 标签留宽到 240：全屏时这里会改写为"分辨率（全屏时无效）"，窄了会截断。
        _resolutionLabel = UiKit.Label(_settingsRoot, "分辨率", 660, 306, 240, 36, 20);
        BuildResolutionOptions();
        _resolutionBox = new OptionButton { MouseDefaultCursorShape = Control.CursorShape.PointingHand };
        UiKit.Place(_resolutionBox, 910, 302, 300, 42);
        foreach (var option in _resolutionOptions) _resolutionBox.AddItem(option.Label);
        _resolutionBox.ItemSelected += index =>
        {
            var picked = _resolutionOptions[(int)index];
            _settings.Width = picked.Width; _settings.Height = picked.Height;
            ApplyDisplay(); PersistSettings();
        };
        _settingsRoot.AddChild(_resolutionBox);

        UiKit.Label(_settingsRoot, "全屏", 660, 362, 160, 36, 20);
        _fullscreenBox = new CheckButton { Text = "全屏显示" };
        // 宽度收窄，否则 CheckButton 会把开关推到控件最右侧，和文字脱节。
        UiKit.Place(_fullscreenBox, 826, 358, 190, 42);
        _fullscreenBox.Toggled += on =>
        {
            _settings.Fullscreen = on; ApplyDisplay(); PersistSettings();
            // 全屏走的是桌面分辨率，分辨率下拉这时不起作用，得当场灰掉而不是等下次开面板。
            RefreshDisplayState();
        };
        _settingsRoot.AddChild(_fullscreenBox);

        UiKit.Label(_settingsRoot, "音频", 660, 418, 200, 34, 24, UiKit.Jade);
        UiKit.Label(_settingsRoot, "音乐音量", 660, 470, 160, 36, 20);
        _musicSlider = VolumeSlider(466); _musicValue = UiKit.Label(_settingsRoot, "", 1145, 466, 90, 30, 18, UiKit.Muted);
        UiKit.Label(_settingsRoot, "音效音量", 660, 520, 160, 36, 20);
        _soundSlider = VolumeSlider(516); _soundValue = UiKit.Label(_settingsRoot, "", 1145, 516, 90, 30, 18, UiKit.Muted);

        UiKit.Label(_settingsRoot, "重置", 660, 576, 200, 34, 24, UiKit.Jade);
        UiKit.Wrapped(_settingsRoot, "重置会清空全部进度：修行、法术、铸造、参悟、剑灵、首杀账本与关卡解锁。画面与音量设置不受影响。", 660, 614, 560, 56, 18, UiKit.Muted);
        _resetButton = UiKit.Button(_settingsRoot, "重置游戏进度", 660, 678, 300, 48, PressReset);
        _resetButton.AddThemeStyleboxOverride("normal", UiKit.Box(DangerBg, 6, DangerEdge));
        _resetButton.AddThemeStyleboxOverride("hover", UiKit.Box(new Color("#5d2f3d"), 6, DangerText));
        _resetButton.AddThemeColorOverride("font_color", DangerText);
        _resetButton.Tip("需要连续点击两次确认，5 秒内未确认会自动取消。");
    }
    private HSlider VolumeSlider(float y)
    {
        var slider = new HSlider { MinValue = 0, MaxValue = 100, Step = 1, Value = 0 };
        UiKit.Place(slider, 830, y, 300, 30);
        slider.ValueChanged += value =>
        {
            if (slider == _musicSlider) _settings.MusicVolume = value; else _settings.SoundVolume = value;
            ApplyAudio(); RefreshVolumeLabels(); PersistSettings();
        };
        _settingsRoot!.AddChild(slider);
        return slider;
    }
    private void RefreshSettings()
    {
        BuildResolutionOptions();
        _resolutionBox.Clear();
        foreach (var option in _resolutionOptions) _resolutionBox.AddItem(option.Label);
        _resolutionBox.Select(Math.Max(0, _resolutionOptions.FindIndex(o => o.Width == _settings.Width && o.Height == _settings.Height)));
        // 必须用 no-signal 版本：ButtonPressed 的 setter 会发出 Toggled，
        // 于是"打开面板"会反过来触发 ApplyDisplay 把窗口重设一遍并落盘——
        // 玩家手动拖出来的窗口尺寸会被悄悄弹回旧的分辨率预设。
        _fullscreenBox.SetPressedNoSignal(_settings.Fullscreen);
        _musicSlider.Value = _settings.MusicVolume;
        _soundSlider.Value = _settings.SoundVolume;
        RefreshVolumeLabels();
        RefreshDisplayState();
        DisarmReset();
    }

    /// <summary>
    /// 全屏时窗口尺寸由屏幕决定，分辨率下拉选什么都没用；不禁用并且不改提示的话，
    /// 玩家会以为"分辨率设置没生效"。
    /// </summary>
    private void RefreshDisplayState()
    {
        if (_resolutionBox is null) return;
        _resolutionBox.Disabled = _settings.Fullscreen;
        _resolutionLabel.Text = _settings.Fullscreen ? "分辨率（全屏时无效）" : "分辨率";
        _resolutionLabel.AddThemeColorOverride("font_color", _settings.Fullscreen ? UiKit.Muted : UiKit.Text);
    }

    /// <summary>
    /// 玩家拖动窗口边缘改尺寸时同步进设置：不同步的话面板显示的"当前分辨率"与真实窗口对不上，
    /// 下次启动还会弹回旧尺寸——那正是"分辨率设置不起效"的观感来源。
    /// 全屏时窗口尺寸由屏幕决定，此时不能覆盖玩家选的分辨率。
    /// </summary>
    private void OnWindowSizeChanged()
    {
        // 无窗口自检下 DisplayServer 是 dummy，读回来的窗口尺寸没有意义。
        if (DisplayServer.GetName() == "headless" || _settings.Fullscreen) return;
        var size = DisplayServer.WindowGetSize();
        if (size.X <= 0 || size.Y <= 0 || (size.X == _settings.Width && size.Y == _settings.Height)) return;
        _settings.Width = size.X; _settings.Height = size.Y;
        _displayDirty = true; _displayPersistDelay = DisplayPersistDelay;
    }
    private void RefreshVolumeLabels()
    {
        _musicValue.Text = $"{_settings.MusicVolume:0}";
        _soundValue.Text = $"{_settings.SoundVolume:0}";
    }
    /// <summary>当前窗口尺寸不在预设列表里时，补一条"当前"项，避免打开面板时选中项对不上。</summary>
    private void BuildResolutionOptions()
    {
        _resolutionOptions.Clear();
        _resolutionOptions.AddRange(Resolutions);
        if (!_resolutionOptions.Any(r => r.Width == _settings.Width && r.Height == _settings.Height))
            _resolutionOptions.Insert(0, ($"{_settings.Width} × {_settings.Height}（当前）", _settings.Width, _settings.Height));
    }

    /// <summary>应用窗口尺寸与显示模式。headless（无窗口自检）下 DisplayServer 是 dummy，改窗口没有意义。</summary>
    private void ApplyDisplay()
    {
        if (DisplayServer.GetName() == "headless") return;
        if (_settings.Fullscreen) { _pendingSize = null; DisplayServer.WindowSetMode(DisplayServer.WindowMode.Fullscreen); return; }
        var target = new Vector2I(_settings.Width, _settings.Height);
        // 从最大化/全屏退回窗口模式时，Windows 会异步把窗口还原成进入之前的尺寸，
        // 同一帧里设的尺寸随后会被那次还原盖掉——表现为"先最大化再改分辨率没反应"。
        // 所以退出非窗口模式后把设尺寸推迟到下一帧，落到还原之后。
        bool leaving = DisplayServer.WindowGetMode() != DisplayServer.WindowMode.Windowed;
        DisplayServer.WindowSetMode(DisplayServer.WindowMode.Windowed);
        if (leaving) _pendingSize = target;
        else ApplyWindowSize(target);
    }
    private void ApplyWindowSize(Vector2I size)
    {
        DisplayServer.WindowSetSize(size);
        GetWindow().MoveToCenter();
    }
    /// <summary>音乐/音效各自一条总线；当前尚无音频内容，接入后自动生效，无需改动此处。</summary>
    private void ApplyAudio()
    {
        SetBusVolume("Music", _settings.MusicVolume);
        SetBusVolume("SFX", _settings.SoundVolume);
    }
    private static void SetBusVolume(string name, double volume)
    {
        int index = AudioServer.GetBusIndex(name);
        if (index < 0)
        {
            AudioServer.AddBus(); index = AudioServer.BusCount - 1;
            AudioServer.SetBusName(index, name);
            AudioServer.SetBusSend(index, "Master");
        }
        // 线性音量便于玩家理解；0 直接静音，避免换算成 -inf dB。
        AudioServer.SetBusVolumeDb(index, volume <= 0 ? -80f : Mathf.LinearToDb((float)(volume / 100)));
    }
    private void PersistSettings() { _settingsStore.Save(_settings); }

    /// <summary>
    /// 重置进度：二次确认。第一次点击只进入待确认状态，5 秒内再点一次才真正清档，
    /// 避免误触直接抹掉进度。
    /// </summary>
    private void PressReset()
    {
        if (_resetArmed <= 0)
        {
            _resetArmed = ResetConfirmSeconds;
            _resetButton.Text = "确认重置？再点一次";
            return;
        }
        ResetProgress();
    }
    private void ResetProgress()
    {
        var config = _game.Config;
        // 先退出技能预览：它会重建 `_game`，而预览页的读数与靶场是照旧会话摆的，留着就是两套东西并存。
        if (_preview is not null) TogglePreview();
        _store.Delete();
        // 重建会话：旧会话的事件订阅随对象一起丢弃，新会话从初始状态开始。
        _game = new GameSession(config);
        _game.PersistRequested += Save;
        _battle.Session = _game;
        // 星图也要跟着归零：平移量不重置的话，重置后只剩根节点可见、画面却还停在上一次那棵长树的右边，
        // 玩家对着一张空白星图找不到那个唯一能点的根。见 TalentMap.ResetTalentView。
        ResetTalentView();
        _levelIds.Clear();
        _gameNotice = "进度已重置。";
        CloseSettings();
        Save();
        ShowPage(0); Refresh();
        // 重置就是"从零开始"，所以序章重播一次。测试模式下不播——那会打乱紧跟着的重置断言。
        if (!_testMode) StartPrologue();
    }
}

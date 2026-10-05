namespace IdleSword.SoundGen;

/// <summary>
/// 全部占位音频的定义。每个函数返回一段浮点样本，交给 <see cref="Synth.Write"/> 落盘。
/// 这些是"能听出差别、能验证接线"的占位素材，不是最终音频设计。
/// </summary>
internal static class Sounds
{
    private const double Beat = 60 / 150.0;   // 150 BPM
    private const int Beats = 16;

    // ── 通用战斗循环曲 ────────────────────────────────────────────────
    /// <summary>
    /// 16 拍循环。主旋律方波 25% 占空比（尖亮），贝斯三角波，噪声做踩镲与底鼓。
    /// 整数拍长度 + 首尾淡入淡出，保证循环接缝不爆音。
    /// </summary>
    public static double[] Battle()
    {
        var buf = Synth.Buffer(Beats * Beat);

        (double Start, double Dur, double Degree)[] lead =
        [
            (0, 1, 0), (1, .5, 1), (1.5, .5, 2), (2, 1, 1), (3, 1, 0),
            (4, 1, -1), (5, .5, 0), (5.5, .5, 1), (6, 2, 0),
            (8, 1, 2), (9, .5, 1), (9.5, .5, 0), (10, 1, 1), (11, 1, 2),
            (12, 1, 4), (13, .5, 2), (13.5, .5, 1), (14, 2, 0),
        ];
        foreach (var (start, duration, degree) in lead)
        {
            double f = Synth.Penta(degree);
            Synth.Tone(buf, start * Beat, duration * Beat * .92, f, f, Synth.Wave.Square, .17, .25, .006, 1.6);
        }

        // 每两拍一个根音：宫 宫 徵 徵 角 角 商 徵。
        double[] bass = [-5, -5, -2, -2, -3, -3, -4, -2];
        for (int i = 0; i < bass.Length; i++)
        {
            double f = Synth.Penta(bass[i]);
            Synth.Tone(buf, i * 2 * Beat, 2 * Beat * .9, f, f, Synth.Wave.Triangle, .30, .5, .008, 1.2);
        }

        for (int i = 0; i < Beats; i++)
        {
            Synth.Tone(buf, i * Beat, .05, 6000, 6000, Synth.Wave.Noise, .07, .5, .001, 5);
            if (i % 4 == 0) Synth.Tone(buf, i * Beat, .12, 220, 60, Synth.Wave.Square, .22, .5, .002, 2.5);
        }
        Synth.FadeEdges(buf);
        return buf;
    }

    // ── 战斗反馈 ──────────────────────────────────────────────────────
    /// <summary>普通命中：一记短促的噪声爆 + 低方波闷响。</summary>
    public static double[] Hit()
    {
        var buf = Synth.Buffer(.14);
        Synth.Tone(buf, 0, .05, 3200, 3200, Synth.Wave.Noise, .42, .5, .001, 4);
        Synth.Tone(buf, 0, .08, 180, 90, Synth.Wave.Square, .34, .5, .002, 3);
        return buf;
    }

    /// <summary>重击：更长更厚的爆音，多叠一层尖方波做"破甲"感。</summary>
    public static double[] HitHeavy()
    {
        var buf = Synth.Buffer(.30);
        Synth.Tone(buf, 0, .16, 2600, 2600, Synth.Wave.Noise, .50, .5, .001, 2.6);
        Synth.Tone(buf, 0, .20, 260, 60, Synth.Wave.Square, .46, .5, .002, 2.2);
        Synth.Tone(buf, 0, .12, 520, 120, Synth.Wave.Square, .22, .125, .002, 2.4);
        return buf;
    }

    /// <summary>击杀：下坠扫频，明确"目标没了"。</summary>
    public static double[] Kill()
    {
        var buf = Synth.Buffer(.34);
        Synth.Tone(buf, 0, .30, 760, 110, Synth.Wave.Square, .38, .5, .003, 2);
        Synth.Tone(buf, 0, .12, 3000, 3000, Synth.Wave.Noise, .24, .5, .001, 3.5);
        return buf;
    }

    // ── 技能释放（按法术 kind，由 SwordSkill.csv 驱动，不额外维护映射表）────
    /// <summary>弹丸类：上扬的"嗖"。</summary>
    public static double[] CastProjectile()
    {
        var buf = Synth.Buffer(.20);
        Synth.Tone(buf, 0, .14, 480, 1700, Synth.Wave.Square, .32, .25, .004, 1.3);
        Synth.Tone(buf, 0, .08, 2200, 4200, Synth.Wave.Noise, .10, .5, .002, 3);
        return buf;
    }

    /// <summary>单体类：高音下坠 + 落点爆。</summary>
    public static double[] CastTarget()
    {
        var buf = Synth.Buffer(.32);
        Synth.Tone(buf, 0, .10, 1500, 300, Synth.Wave.Square, .32, .125, .003, 2);
        Synth.Tone(buf, .09, .16, 1800, 400, Synth.Wave.Noise, .46, .5, .001, 2.6);
        Synth.Tone(buf, .09, .18, 300, 70, Synth.Wave.Square, .34, .5, .002, 2.2);
        return buf;
    }

    /// <summary>范围类：低频轰鸣 + 持续嗡，撑出一片区域感。</summary>
    public static double[] CastGround()
    {
        var buf = Synth.Buffer(.62);
        Synth.Tone(buf, 0, .55, 260, 120, Synth.Wave.Noise, .30, .5, .01, 1.2);
        Synth.Tone(buf, 0, .55, 92, 88, Synth.Wave.Triangle, .34, .5, .02, .9);
        Synth.Tone(buf, .02, .45, 184, 176, Synth.Wave.Square, .14, .5, .03, 1.1);
        return buf;
    }

    /// <summary>增益类：明亮的上行琶音。buff 技能不产生飞行物，全靠音效提示已生效。</summary>
    public static double[] CastBuff()
    {
        var buf = Synth.Buffer(.40);
        double[] notes = [0, 2, 4, 5];
        for (int i = 0; i < notes.Length; i++)
        {
            double f = Synth.Penta(notes[i] + 2);
            Synth.Tone(buf, i * .07, .22, f, f, Synth.Wave.Square, .26, .25, .006, 2.4);
        }
        return buf;
    }

    /// <summary>召唤类：空灵上冲 + 一层逐渐涌起的气流。</summary>
    public static double[] CastSummon()
    {
        var buf = Synth.Buffer(.56);
        double[] notes = [4, 5, 7, 9];
        for (int i = 0; i < notes.Length; i++)
        {
            double f = Synth.Penta(notes[i]);
            Synth.Tone(buf, i * .06, .30, f, f, Synth.Wave.Triangle, .24, .5, .008, 1.8);
        }
        Synth.Tone(buf, .18, .30, 900, 2400, Synth.Wave.Square, .16, .125, .02, 1.4);
        Synth.Tone(buf, 0, .50, 60, 200, Synth.Wave.Noise, .10, .5, .12, 1.0);
        return buf;
    }

    // ── 界面与养成 ────────────────────────────────────────────────────
    public static double[] Click()
    {
        var buf = Synth.Buffer(.06);
        Synth.Tone(buf, 0, .035, 1300, 950, Synth.Wave.Square, .30, .5, .001, 3);
        return buf;
    }

    /// <summary>面板开合：两声递进短音。</summary>
    public static double[] Panel()
    {
        var buf = Synth.Buffer(.18);
        Synth.Tone(buf, 0, .07, 700, 760, Synth.Wave.Square, .26, .25, .004, 2.4);
        Synth.Tone(buf, .06, .11, 1050, 1140, Synth.Wave.Square, .24, .25, .004, 2.2);
        return buf;
    }

    /// <summary>养成成功：两级上行，简单明确。</summary>
    public static double[] Buy()
    {
        var buf = Synth.Buffer(.30);
        foreach (var (start, degree) in new[] { (0.0, 2.0), (.08, 4.0) })
        {
            double f = Synth.Penta(degree);
            Synth.Tone(buf, start, start == 0 ? .09 : .20, f, f, Synth.Wave.Square, .28, .25, .004, 2);
        }
        return buf;
    }

    /// <summary>操作失败：两个相近低频相拍，做出"嗡嗡"的否决感。</summary>
    public static double[] Deny()
    {
        var buf = Synth.Buffer(.24);
        Synth.Tone(buf, 0, .20, 148, 132, Synth.Wave.Square, .30, .5, .004, 1.6);
        Synth.Tone(buf, 0, .20, 157, 140, Synth.Wave.Square, .22, .5, .004, 1.6);
        return buf;
    }

    /// <summary>首杀灵核：四音上行的短号角。</summary>
    public static double[] Reward()
    {
        var buf = Synth.Buffer(.60);
        double[] notes = [0, 2, 4, 5];
        for (int i = 0; i < notes.Length; i++)
        {
            double f = Synth.Penta(notes[i] + 2);
            Synth.Tone(buf, i * .09, .34, f, f, Synth.Wave.Square, .28, .5, .005, 1.8);
        }
        return buf;
    }

    /// <summary>突破境界：上行五音后落在和弦上，比普通成功音更长更重。</summary>
    public static double[] Breakthrough()
    {
        var buf = Synth.Buffer(1.10);
        double[] notes = [0, 2, 4, 5, 7];
        for (int i = 0; i < notes.Length; i++)
        {
            double f = Synth.Penta(notes[i]);
            Synth.Tone(buf, i * .10, .30, f, f, Synth.Wave.Square, .26, .25, .005, 2);
        }
        foreach (var (degree, wave) in new[] { (2.0, Synth.Wave.Square), (4.0, Synth.Wave.Triangle), (7.0, Synth.Wave.Triangle) })
        {
            double f = Synth.Penta(degree);
            Synth.Tone(buf, .52, .55, f, f, wave, .24, .5, .02, 1.2);
        }
        return buf;
    }
}

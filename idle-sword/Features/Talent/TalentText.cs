using IdleSword.Core;

namespace IdleSword.Features;

/// <summary>
/// 天赋节点效果的**文案翻译**：`effect` + `effect_per_level` → 一句中文。
///
/// 刻意放在 Features 而不是 UI——这样纯逻辑自检就能断言"**每一种被用到的 `effect` 都有文案**"。
/// 效果 id 直接漏到界面上、或者显示成空白，是典型的静默失败：数值配好了、忘了写文案，
/// 玩起来只觉得"这个节点没用"，不会报任何错。下一轮新增效果时，这里漏写会被自检当场抓住。
/// </summary>
public static class TalentText
{
    /// <summary>把一行天赋的效果翻成玩家能读的一句话。未知取值由加载期校验拦住，这里给个兜底。</summary>
    public static string DescribeEffect(CsvRow node)
    {
        string effect = node.Text("effect");
        double per = node.Number("effect_per_level");
        return effect switch
        {
            "atk" => $"攻击加成 +{per:P0}/级",
            "hp" => $"气血加成 +{per:P0}/级",
            // 固定值与百分比是两种读法。**修行节点现在一律投固定值**——前期数值小，
            // "再来一下能不能打死"要能一眼算出来；百分比那条路径保留着，后期要投再投。
            "atk_flat" => $"攻击 +{per:0.##}/级",
            "hp_flat" => $"气血 +{per:0.##}/级",
            "drop_flat" => $"每只怪多掉 {per:0.##} 灵石",
            "auto_intent" => "解锁在线自动参悟",
            "auto_basic" => "激活自动攻击（不用再点）",
            "ranged_basic" => "普攻变为远程·剑气",
            "realm_system" => "开启「法术」·可修习剑诀",
            "forge_system" => "开启「铸造」·可打造兵器",
            "intent_system" => "开启「参悟」·可凝练剑意",
            "none" => "暂无效果（占位节点）",
            _ => $"效果 {effect} +{per:0.##}/级",
        };
    }
}

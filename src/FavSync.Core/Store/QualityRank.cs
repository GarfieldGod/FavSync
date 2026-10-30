namespace FavSync.Core.Store;

/// <summary>
/// 画质比较。**不只看 q 值，还看编码**（用户拍板）。
///
/// 同一个 q 值下，三种编码的码率效率差得很远（实测：手机 q116/HEVC 23.8 MB
/// vs 库里 q116/AVC 35.5 MB，同画质小 33%），所以"同档不同编码"不是等价的两份：
/// HEVC / AV1 那份才是更好的原件。
///
/// 规则：
/// <list type="number">
/// <item>先比 q；q 不同就是画质高低，与编码无关。</item>
/// <item>q 相同再比编码等级：<c>HEVC = AV1 &gt; AVC</c>。</item>
/// <item>HEVC 与 AV1 **并列**，互不替换 —— 否则两边来回换主，每次同步都在搬几百 MB。</item>
/// </list>
///
/// 不做的事：不判浏览器兼容性。播放只发生在管理页的浏览器里，
/// 播放器是否认 HEVC 不参与"哪份更好"的决策（用户明确）。
/// </summary>
public static class QualityRank
{
    /// <summary>编码等级：AVC(7)=0，HEVC(12)/AV1(13)=1。未知编码按 AVC 对待（保守，不凭空缺主）。</summary>
    public static int CodecRank(int? codecid) => codecid switch
    {
        12 => 1,   // HEVC / H.265
        13 => 1,   // AV1
        _ => 0,    // AVC(7) 及其他未知
    };

    /// <summary>
    /// 三态比较：<c>&gt;0</c> 表示 a 更好，<c>&lt;0</c> 表示 b 更好，<c>0</c> 表示等价
    /// （含"任一侧缺档位信息"——缺信息时不判优劣，避免把未知当成差）。
    /// </summary>
    public static int Compare(int? qa, int? ca, int? qb, int? cb)
    {
        if (qa is null || qb is null) return 0;
        if (qa.Value != qb.Value) return qa.Value.CompareTo(qb.Value);
        return CodecRank(ca).CompareTo(CodecRank(cb));
    }

    /// <summary>a 是否严格优于 b（等价时返回 false：同档同编码等级不换主）。</summary>
    public static bool IsBetter(int? qa, int? ca, int? qb, int? cb) => Compare(qa, ca, qb, cb) > 0;

    /// <summary>编码名（UI 文案与账目说明共用）。</summary>
    public static string CodecName(int? codecid) => codecid switch
    {
        7 => "AVC",
        12 => "HEVC",
        13 => "AV1",
        _ => codecid is { } c ? $"c{c}" : "未知",
    };
}

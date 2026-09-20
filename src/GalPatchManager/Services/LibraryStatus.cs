namespace GalPatchManager.Services;

/// <summary>
/// 「库标签检测」：把库里能查到的关键状态汇总成一句可读文本。
///
/// 目前检测：
///   1. 是否原生支持中文 —— 直接决定这个游戏**还要不要打汉化补丁**；
///   2. 是否有待处理补丁；
///   3. 补丁是否已安装过。
///
/// 数据来源：Steam 官方 <c>supported_languages</c> + 本程序的待处理/历史记录。
/// 没抓到元数据时如实显示「未检测」，不瞎猜。
/// </summary>
public static class LibraryStatus
{
    /// <summary>
    /// 判断支持语言文本里是否有中文（简体 / 繁体 / 通用 Chinese）。
    /// 命中 "Chinese"、"Simplified Chinese"、"Traditional Chinese"、"中文"、"简体"、"繁体" 都算。
    /// </summary>
    public static bool DetectChinese(string? supportedLanguages)
    {
        if (string.IsNullOrWhiteSpace(supportedLanguages)) return false;

        var t = supportedLanguages;

        return t.Contains("Chinese", StringComparison.OrdinalIgnoreCase)
            || t.Contains("中文", StringComparison.Ordinal)
            || t.Contains("简体", StringComparison.Ordinal)
            || t.Contains("繁体", StringComparison.Ordinal);
    }

    /// <summary>
    /// 生成「库检测」列的显示文本。
    /// </summary>
    /// <param name="info">该游戏的缓存元数据；null 表示还没检测过。</param>
    /// <param name="pendingPatches">待处理补丁数量。</param>
    /// <param name="installedPatches">已成功安装的补丁数量。</param>
    public static string Describe(GameTagInfo? info, int pendingPatches, int installedPatches)
    {
        var parts = new List<string>();

        var languages = info?.SupportedLanguages;

        if (string.IsNullOrWhiteSpace(languages))
        {
            parts.Add("未检测");
        }
        else if (DetectChinese(languages))
        {
            parts.Add("原生中文");
        }
        else
        {
            parts.Add("无中文");
        }

        if (pendingPatches > 0) parts.Add($"待处理补丁 {pendingPatches}");
        if (installedPatches > 0) parts.Add($"已装 {installedPatches}");

        return string.Join(" | ", parts);
    }
}

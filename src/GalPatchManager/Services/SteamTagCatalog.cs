using GalPatchManager.Models;

namespace GalPatchManager.Services;

/// <summary>
/// Steam 社区标签目录。
///
/// 为什么需要它：Steam 官方 <c>appdetails</c> 接口**只给 genres**（Action/Adventure/RPG…），
/// 而「视觉小说」「Anime」「Hentai」这些是**社区标签**，官方接口没有。
///
/// 实测确认：
///   * 商店页 <c>/app/&lt;id&gt;/</c> 的标签是 JS 异步渲染的，HTML 里拿不到标签名；
///   * 但搜索接口 <c>/search/results/?json=1</c> 的每一项都带
///     <c>data-ds-tagids="[3799,9551,...]"</c>，即该游戏的完整社区标签 ID 列表。
///
/// 所以策略是：按游戏名搜索 → 取 tagids → 用本表把 ID 翻成名字。
/// 标签 ID 取自 Steam 官方全局标签列表。
/// </summary>
public static class SteamTagCatalog
{
    /// <summary>常用标签 ID -> 名称。</summary>
    private static readonly Dictionary<int, string> Known = new()
    {
        // ---- 与 Galgame / 日系 AVG 最相关 ----
        [3799] = "Visual Novel",
        [9551] = "Dating Sim",
        [4947] = "Romance",
        [4085] = "Anime",
        [9130] = "Hentai",
        [6650] = "Nudity",
        [12095] = "Sexual Content",
        [31579] = "Otome",
        [11014] = "Interactive Fiction",
        [4486] = "Choose Your Own Adventure",
        [1742] = "Story Rich",
        [6971] = "Multiple Endings",
        [5608] = "Emotional",
        [5984] = "Drama",
        [8369] = "Investigation",
        [5716] = "Mystery",
        [5186] = "Psychological",
        [4064] = "Thriller",
        [5900] = "Walking Simulator",
        [31275] = "Text-Based",
        [15172] = "Conversation",
        [7702] = "Narrative",
        [9592] = "Dynamic Narration",
        [5094] = "Narration",
        [8461] = "Well-Written",

        // ---- 类型 / 玩法 ----
        [492] = "Indie",
        [19] = "Action",
        [21] = "Adventure",
        [597] = "Casual",
        [599] = "Simulation",
        [122] = "RPG",
        [9] = "Strategy",
        [4434] = "JRPG",
        [4106] = "Action-Adventure",
        [4231] = "Action RPG",
        [1664] = "Puzzle",
        [6129] = "Logic",
        [1625] = "Platformer",
        [1708] = "Tactical",
        [1741] = "Turn-Based Strategy",
        [4325] = "Turn-Based Combat",
        [1666] = "Card Game",
        [5577] = "RPGMaker",
        [10235] = "Life Sim",
        [87918] = "Farming Sim",

        // ---- 表现 / 其他 ----
        [3871] = "2D",
        [4191] = "3D",
        [3964] = "Pixel Graphics",
        [6815] = "Hand-drawn",
        [1756] = "Great Soundtrack",
        [1721] = "Psychological Horror",
        [1667] = "Horror",
        [4345] = "Gore",
        [4667] = "Violent",
        [4182] = "Singleplayer",
        [1685] = "Co-op",
    };

    /// <summary>把标签 ID 翻成名字；未知 ID 返回 null。</summary>
    public static string? NameOf(int tagId) => Known.TryGetValue(tagId, out var n) ? n : null;

    /// <summary>把标签 ID 列表翻成名字（只保留已知的）。</summary>
    public static List<string> NamesOf(IEnumerable<int> tagIds)
    {
        var list = new List<string>();

        foreach (var id in tagIds)
        {
            var n = NameOf(id);

            if (n is not null && !list.Contains(n, StringComparer.OrdinalIgnoreCase)) list.Add(n);
        }

        return list;
    }

    /// <summary>常用标签，供界面下拉选择。</summary>
    public static List<string> CommonTagNames() => new()
    {
        "Visual Novel", "Dating Sim", "Romance", "Anime", "Otome",
        "Hentai", "Sexual Content", "Nudity",
        "Interactive Fiction", "Choose Your Own Adventure", "Story Rich",
        "Multiple Endings", "Emotional", "Drama", "Investigation", "Mystery",
        "Adventure", "RPG", "JRPG", "Puzzle", "Casual", "Indie", "Simulation",
    };

    /// <summary>默认的「社区标签 -> 游戏类型」映射（顺序即优先级）。</summary>
    public static Dictionary<string, string> DefaultTagTypeMapping() =>
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["Visual Novel"] = "视觉小说",
            ["Dating Sim"] = "视觉小说",
            ["Otome"] = "视觉小说",
            ["Interactive Fiction"] = "视觉小说",
            ["Choose Your Own Adventure"] = "视觉小说",
            ["JRPG"] = "角色扮演",
            ["RPG"] = "角色扮演",
            ["Adventure"] = "冒险",
            ["Puzzle"] = "益智",
            ["Casual"] = "休闲",
            ["Simulation"] = "模拟",
            ["Life Sim"] = "模拟",
            ["Farming Sim"] = "模拟",
            ["Strategy"] = "策略",
            ["Action"] = "动作",
            ["Indie"] = "独立",
        };

    /// <summary>
    /// 按映射表把社区标签折算成游戏类型（顺序即优先级）。
    /// </summary>
    public static string? MapToGameType(
        IEnumerable<string> tags,
        Dictionary<string, string> mapping,
        IReadOnlyList<string> knownTypes)
    {
        var list = tags.Where(t => !string.IsNullOrWhiteSpace(t)).ToList();

        if (list.Count == 0) return null;

        foreach (var kv in mapping)
        {
            if (string.IsNullOrWhiteSpace(kv.Key) || string.IsNullOrWhiteSpace(kv.Value)) continue;

            if (list.Any(t => string.Equals(t.Trim(), kv.Key.Trim(), StringComparison.OrdinalIgnoreCase)))
            {
                var type = kv.Value.Trim();

                if (knownTypes.Count == 0 || knownTypes.Contains(type, StringComparer.OrdinalIgnoreCase))
                {
                    return type;
                }
            }
        }

        return null;
    }
}

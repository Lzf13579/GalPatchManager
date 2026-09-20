using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace GalPatchManager.Services;

/// <summary>单个游戏的标签/类型归类信息。</summary>
public sealed class GameTagInfo
{
    /// <summary>缓存结构版本。字段扩充时递增，用于识别老缓存并重新抓取。</summary>
    public const int CurrentSchema = 3;

    /// <summary>写入本条目时的结构版本。</summary>
    public int Schema { get; set; } = CurrentSchema;

    public string Name { get; set; } = "";

    /// <summary>官方 genres（来自 Steam 商店接口）。</summary>
    public List<string> Genres { get; set; } = new();

    /// <summary>
    /// **Steam 社区标签**（Visual Novel / Anime / Hentai 这类）。
    /// 官方接口不提供，是通过搜索接口的 data-ds-tagids 抓到的。
    /// </summary>
    public List<string> CommunityTags { get; set; } = new();

    /// <summary>社区标签的原始 ID（未识别出名字的也会保留，便于排查）。</summary>
    public List<int> CommunityTagIds { get; set; } = new();

    /// <summary>官方 categories。</summary>
    public List<string> Categories { get; set; } = new();

    /// <summary>
    /// 官方支持语言原文，例如 "English, Simplified Chinese, Japanese"。
    /// 用于判断游戏**是否原生支持中文**（这直接决定要不要打汉化补丁）。
    /// </summary>
    public string SupportedLanguages { get; set; } = "";

    /// <summary>开发商。</summary>
    public List<string> Developers { get; set; } = new();

    /// <summary>发行商。</summary>
    public List<string> Publishers { get; set; } = new();

    /// <summary>发行日期文本，例如 "9 Jun, 2024"。</summary>
    public string ReleaseDate { get; set; } = "";

    /// <summary>归一化后的游戏类型。</summary>
    public string Type { get; set; } = "";

    /// <summary>归类依据说明。</summary>
    public string Reason { get; set; } = "";

    /// <summary>该类型是否为自动得出（false = 用户指定）。</summary>
    public bool IsAutomatic { get; set; } = true;

    public DateTime UpdatedAt { get; set; } = DateTime.Now;

    /// <summary>缓存是否由旧版结构写入（缺新字段，需要重新抓取）。</summary>
    public bool IsLegacySchema => Schema < CurrentSchema;
}

/// <summary>
/// 游戏标签/类型缓存：%AppData%\GalPatchManager\steam-appinfo.json
///
/// 把 genres 与归类结果落盘，好处：
///   * 不会每次刷新都去请求网络；
///   * 断网也能用之前抓到的结果；
///   * 文件是普通 JSON，用户可以直接查看/编辑。
/// </summary>
public static class TagCache
{
    private static readonly object Gate = new();
    private static Dictionary<string, GameTagInfo>? _cache;

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    private static string FilePath => Path.Combine(Paths.AppDataRoot, "steam-appinfo.json");

    private static Dictionary<string, GameTagInfo> Load()
    {
        lock (Gate)
        {
            if (_cache is not null) return _cache;

            try
            {
                if (File.Exists(FilePath))
                {
                    var text = File.ReadAllText(FilePath, Encoding.UTF8);

                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        var loaded = JsonSerializer.Deserialize<Dictionary<string, GameTagInfo>>(text, Options);

                        if (loaded is not null)
                        {
                            _cache = new Dictionary<string, GameTagInfo>(loaded, StringComparer.OrdinalIgnoreCase);
                            return _cache;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Warn($"读取标签缓存失败，将重建: {ex.Message}");
            }

            _cache = new Dictionary<string, GameTagInfo>(StringComparer.OrdinalIgnoreCase);
            return _cache;
        }
    }

    public static void Save()
    {
        lock (Gate)
        {
            try
            {
                Paths.EnsureCreated();

                var json = JsonSerializer.Serialize(
                    _cache ?? new Dictionary<string, GameTagInfo>(StringComparer.OrdinalIgnoreCase), Options);

                var tmp = FilePath + ".tmp";

                File.WriteAllText(tmp, json, new UTF8Encoding(false));

                if (File.Exists(FilePath)) File.Replace(tmp, FilePath, null);
                else File.Move(tmp, FilePath);
            }
            catch (Exception ex)
            {
                Log.Warn($"保存标签缓存失败: {ex.Message}");
            }
        }
    }

    public static GameTagInfo? Get(string stableKey)
    {
        var c = Load();
        return c.TryGetValue(stableKey, out var v) ? v : null;
    }

    public static void Set(string stableKey, GameTagInfo info)
    {
        var c = Load();

        info.UpdatedAt = DateTime.Now;
        c[stableKey] = info;

        Save();
    }

    public static int Count => Load().Count;

    public static DateTime? LastUpdated
    {
        get
        {
            var c = Load();

            return c.Count == 0 ? null : c.Values.Max(v => v.UpdatedAt);
        }
    }

    public static void Clear()
    {
        lock (Gate)
        {
            _cache = new Dictionary<string, GameTagInfo>(StringComparer.OrdinalIgnoreCase);

            try
            {
                if (File.Exists(FilePath)) File.Delete(FilePath);
            }
            catch (Exception ex)
            {
                Log.Warn($"清除标签缓存失败: {ex.Message}");
            }
        }
    }
}

using System.Text.Json.Serialization;

namespace GalPatchManager.Models;

/// <summary>
/// 全局配置。序列化到 %AppData%\GalPatchManager\config.json。
/// </summary>
public sealed class AppSettings
{
    // ---------------- 通用 ----------------

    /// <summary>额外的 Steam 库路径（用户手动补充，用于补救自动识别失败的库）。</summary>
    public List<string> ExtraSteamLibraryPaths { get; set; } = new();

    /// <summary>用户手动添加的游戏。</summary>
    public List<GameEntry> ManualGames { get; set; } = new();

    /// <summary>从 Steam 识别结果中隐藏的 AppID。</summary>
    public List<string> HiddenAppIds { get; set; } = new();

    // ---------------- 下载监控 ----------------

    /// <summary>监控的下载目录。默认是本程序自己的「放这里」文件夹，而不是系统下载目录。</summary>
    public string DownloadFolder { get; set; } = "";

    /// <summary>是否启用下载目录监控。</summary>
    public bool MonitorEnabled { get; set; } = true;

    /// <summary>
    /// 只处理新增/变化的文件（推荐）。
    ///
    /// 开启后：监控目录里**本来就存在**的 zip/7z/rar/exe 不会被当成待处理补丁，
    /// 只有建立基线之后新出现或内容有变化的文件才会被处理。
    /// 关闭后：目录里所有符合扩展名的文件都会被列出（就会把普通软件安装包也列进来）。
    /// </summary>
    public bool OnlyNewFiles { get; set; } = true;

    /// <summary>小于该体积的文件直接忽略（MB）。用于过滤 .url / .torrent / 说明文件等。</summary>
    public int MinFileSizeMB { get; set; } = 1;

    /// <summary>轮询间隔（秒）。</summary>
    public int PollIntervalSeconds { get; set; } = 3;

    /// <summary>连续多少次轮询大小不变才判定下载结束。</summary>
    public int StableChecksRequired { get; set; } = 3;

    /// <summary>
    /// 判定完成前要求文件至少安静多少秒。
    /// 仅靠“次数”在轮询间隔很短时容易把“变化几次后暂停”的下载误判为完成，因此叠加时长条件。
    /// </summary>
    public int MinQuietSeconds { get; set; } = 2;

    /// <summary>要忽略的临时扩展名（不带点，全部小写）。</summary>
    public List<string> IgnoredExtensions { get; set; } = new()
    {
        "crdownload", "part", "tmp", "partial", "download", "!ut", "opdownload", "td", "filepart",
    };

    /// <summary>要忽略的文件名后缀，例如 ".7z.tmp"。</summary>
    public List<string> IgnoredNameSuffixes { get; set; } = new()
    {
        ".tmp", ".part", ".crdownload", ".!ut", ".opdownload",
    };

    /// <summary>视为补丁的扩展名。</summary>
    public List<string> PatchExtensions { get; set; } = new()
    {
        "zip", "7z", "rar", "exe",
    };

    // ---------------- 安装 ----------------

    /// <summary>覆盖目标文件前是否自动备份。</summary>
    public bool BackupBeforeOverwrite { get; set; } = true;

    /// <summary>备份保留天数；超出后清理。</summary>
    public int BackupRetentionDays { get; set; } = 30;

    /// <summary>安装失败时是否自动回滚已完成的复制。</summary>
    public bool AutoRollbackOnFailure { get; set; } = true;

    /// <summary>安装成功后是否把补丁文件移动到存档目录。</summary>
    public bool ArchiveInstalledPatches { get; set; } = true;

    /// <summary>已安装补丁的存档目录。</summary>
    public string InstalledArchiveFolder { get; set; } = "";

    /// <summary>安装时为压缩包内所有文件自动补一层根目录（当压缩包无根目录时）。</summary>
    public bool AutoWrapArchiveRoot { get; set; } = true;

    // ---------------- 查找补丁 ----------------

    /// <summary>当前选用的搜索方式：Browser / Searxng / CustomJson。</summary>
    public string SearchProvider { get; set; } = "Browser";

    /// <summary>SearXNG 实例地址，例如 http://127.0.0.1:8888 。</summary>
    public string SearxngBaseUrl { get; set; } = "";

    /// <summary>自定义 JSON 搜索接口 URL 模板，必须含 {query} 占位符。</summary>
    public string CustomSearchUrlTemplate { get; set; } = "";

    /// <summary>自定义接口的 API Key（可留空）。</summary>
    public string CustomSearchApiKey { get; set; } = "";

    /// <summary>API Key 附加方式：None / Header / Query 。</summary>
    public string CustomSearchApiKeyMode { get; set; } = "None";

    /// <summary>Header 名称或 Query 参数名。</summary>
    public string CustomSearchApiKeyName { get; set; } = "";

    /// <summary>JSON 结果数组的路径，默认自动探测常见字段。</summary>
    public string CustomSearchResultsPath { get; set; } = "";

    /// <summary>搜索关键词模板，{0} 为游戏名。</summary>
    public List<string> SearchKeywordTemplates { get; set; } = new()
    {
        "{0} 汉化补丁",
        "{0} 中文补丁",
        "{0} 修复补丁",
        "{0} 汉化 patch",
    };

    /// <summary>白名单域名，命中的结果会被标记为可信候选。</summary>
    public List<string> WhitelistedHosts { get; set; } = new();

    /// <summary>白名单完整网址。</summary>
    public List<string> WhitelistedUrls { get; set; } = new();

    /// <summary>搜索结果最多保留条数。</summary>
    public int SearchResultLimit { get; set; } = 50;

    /// <summary>网络请求超时（秒）。</summary>
    public int HttpTimeoutSeconds { get; set; } = 20;

    // ---------------- EXE 可信规则 ----------------

    /// <summary>可信的 EXE 安装规则。</summary>
    public List<ExeTrustRule> ExeTrustRules { get; set; } = new();

    /// <summary>即使可信也要求用户确认（默认开启，最安全）。</summary>
    public bool AlwaysConfirmExe { get; set; } = true;

    // ---------------- 游戏类型 ----------------

    /// <summary>用户自定义的游戏类型（内置类型之外的）。</summary>
    public List<string> CustomGameTypes { get; set; } = new();

    /// <summary>
    /// 游戏类型的用户指定值。键为 <c>app:&lt;AppID&gt;</c> 或 <c>dir:&lt;安装目录&gt;</c>。
    /// 用独立映射是为了让 Steam 游戏刷新（条目重建）后类型不丢失。
    /// </summary>
    public Dictionary<string, string> GameTypeOverrides { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 启发式自动识别的结果缓存（键同上）。
    /// 与 <see cref="GameTypeOverrides"/> 分开保存，保证「覆盖表 = 用户意图」的语义不被自动结果污染。
    /// </summary>
    public Dictionary<string, string> AutoDetectedTypes { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 是否允许联网抓取 Steam 商店元数据（genres）来辅助归类。
    /// 默认开启；抓取失败会自动回退到本地引擎识别，不影响使用。
    /// </summary>
    public bool EnableSteamMetadata { get; set; } = true;

    /// <summary>
    /// 联网方式：
    ///   Auto         = 先直连，失败再试系统代理（默认，兼容性最好）
    ///   Direct       = 只直连（不使用代理）
    ///   SystemProxy  = 只使用系统代理设置
    ///   CustomProxy  = 只使用 <see cref="CustomProxyUrl"/> 指定的代理
    /// </summary>
    public string NetworkMode { get; set; } = "Auto";

    /// <summary>自定义代理地址，例如 http://127.0.0.1:7897 。</summary>
    public string CustomProxyUrl { get; set; } = "";

    /// <summary>元数据缓存保留天数，超过后才会重新抓取。</summary>
    public int MetadataCacheDays { get; set; } = 14;

    /// <summary>并发抓取数量（太大容易被 Steam 限流）。</summary>
    public int MetadataConcurrency { get; set; } = 4;

    /// <summary>是否在"自动归类"时启用本地引擎特征识别。</summary>
    public bool EnableHeuristicType { get; set; } = true;

    /// <summary>是否启用"按游戏名关键词"归类（纯离线，依据游戏名/目录名）。</summary>
    public bool EnableNameTypeRule { get; set; } = true;

    /// <summary>
    /// 是否抓取 **Steam 社区标签**（Visual Novel / Anime / Hentai 这类）。
    ///
    /// 官方 appdetails 接口**没有**社区标签；本程序改查搜索接口的
    /// <c>data-ds-tagids</c> 来获取。因为要按游戏逐个请求，开销比 genres 大，
    /// 所以单独给一个开关（默认开启）。
    /// </summary>
    public bool EnableCommunityTags { get; set; } = true;

    /// <summary>
    /// 社区标签 -> 游戏类型映射。**顺序即优先级**。
    /// 「视觉小说」就靠这里的 Visual Novel 标签命中 —— 官方 genres 里没有这一项。
    /// </summary>
    public Dictionary<string, string> TagTypeMapping { get; set; } = Services.SteamTagCatalog.DefaultTagTypeMapping();

    /// <summary>社区标签抓取的间隔（毫秒），避免被 Steam 限流。</summary>
    public int TagFetchDelayMs { get; set; } = 400;

    /// <summary>
    /// 游戏名关键词 -> 类型。**顺序即优先级**（先命中的先采用）。
    /// 纯离线规则，用户可在设置里自行增删。
    ///
    /// 注意：老版本配置文件里没有这个字段，ApplyDefaults 会补上默认值。
    /// </summary>
    public Dictionary<string, string> NameTypeRules { get; set; } = DefaultNameTypeRules();

    /// <summary>默认的名称关键词规则（顺序即优先级）。</summary>
    public static Dictionary<string, string> DefaultNameTypeRules() =>
        new(StringComparer.OrdinalIgnoreCase)
        {
            // 中文/日文里能明确指向视觉小说的词。
            // 注意：Steam 官方 genres 里**没有**「视觉小说」这一项（那是社区标签），
            // 所以日系 AVG 往往只能落到「冒险」；这里靠名称关键词补一层。
            ["视觉小说"] = "视觉小说",
            ["ビジュアルノベル"] = "视觉小说",
            ["ノベルゲーム"] = "视觉小说",
            ["ギャルゲー"] = "视觉小说",
            ["美少女ゲーム"] = "视觉小说",
            ["エロゲ"] = "视觉小说",
            ["galgame"] = "视觉小说",
            ["恋爱冒险"] = "视觉小说",
            ["恋爱游戏"] = "视觉小说",
            ["文字冒险"] = "视觉小说",
            ["乙女游戏"] = "视觉小说",

            ["ADV"] = "冒险",
            ["アドベンチャー"] = "冒险",

            ["RPG"] = "角色扮演",
            ["ロールプレイング"] = "角色扮演",

            ["パズル"] = "益智",
            ["Puzzle"] = "益智",

            ["シミュレーション"] = "模拟",
            ["Simulation"] = "模拟",

            ["アクション"] = "动作",
        };

    /// <summary>
    /// 商店 genre -> 游戏类型的映射。**字典顺序即优先级**（先命中的先采用）。
    /// 用户可以在配置文件或设置页里自行调整。
    ///
    /// 注意：老版本配置文件里没有这个字段，反序列化后会是空表。
    /// ApplyDefaults 里会检测空表并补上默认映射，否则归类会全部落空。
    /// </summary>
    public Dictionary<string, string> GenreTypeMapping { get; set; } = DefaultGenreTypeMapping();

    /// <summary>默认的 genre 映射（顺序即优先级）。</summary>
    public static Dictionary<string, string> DefaultGenreTypeMapping() =>
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["Adventure"] = "冒险",
            ["RPG"] = "角色扮演",
            ["Action"] = "动作",
            ["Strategy"] = "策略",
            ["Simulation"] = "模拟",
            ["Casual"] = "休闲",
            ["Indie"] = "独立",
        };

    // ---------------- 关键词匹配 ----------------

    /// <summary>用户自定义的匹配规则。</summary>
    public List<MatchRuleEntry> MatchRules { get; set; } = new();

    // ---------------- 界面 ----------------

    /// <summary>窗口尺寸，退出时保存。</summary>
    public int WindowWidth { get; set; } = 1180;

    public int WindowHeight { get; set; } = 760;

    /// <summary>是否在安装完成后打开游戏目录。</summary>
    public bool OpenGameFolderAfterInstall { get; set; }

    /// <summary>自动运行可信 EXE 的开关（默认关闭）。</summary>
    public bool AutoRunTrustedExe { get; set; }

    /// <summary>配置文件结构版本，便于以后迁移。</summary>
    public int ConfigVersion { get; set; } = CurrentConfigVersion;

    /// <summary>当前配置结构版本。</summary>
    public const int CurrentConfigVersion = 2;

    /// <summary>
    /// 程序自己的「放这里」目录：%AppData%\GalPatchManager\DropFolder
    /// </summary>
    public static string DefaultDropFolder =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "GalPatchManager",
            "DropFolder");

    /// <summary>补齐默认路径。</summary>
    public void ApplyDefaults(string appDataRoot)
    {
        // 反序列化老配置时这些集合可能为 null，统一兜底，避免各处判空
        CustomGameTypes ??= new List<string>();
        MatchRules ??= new List<MatchRuleEntry>();
        ExeTrustRules ??= new List<ExeTrustRule>();
        ManualGames ??= new List<GameEntry>();
        GameTypeOverrides ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        AutoDetectedTypes ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // 关键：老版本配置文件里没有这两个映射表，反序列化后会是**空表**（不是 null）。
        // 如果放着不管，归类时所有 genre / 名称规则都匹配不到，会全部落到「未分类」或「其他」。
        // 所以这里检测到空表就补上默认值。
        if (GenreTypeMapping.Count == 0)
        {
            GenreTypeMapping = DefaultGenreTypeMapping();
            Services.Log.Info("配置迁移：已补上默认的 genre → 类型映射表");
        }

        if (NameTypeRules.Count == 0)
        {
            NameTypeRules = DefaultNameTypeRules();
            Services.Log.Info("配置迁移：已补上默认的名称关键词规则");
        }

        // 社区标签映射：老配置没有这个字段，同样需要补默认值，
        // 否则「视觉小说」这个关键分类会永远匹配不到。
        if (TagTypeMapping is null || TagTypeMapping.Count == 0)
        {
            TagTypeMapping = Services.SteamTagCatalog.DefaultTagTypeMapping();
            Services.Log.Info("配置迁移：已补上默认的社区标签 → 类型映射表");
        }

        // 迁移：早期版本默认监控系统「下载」目录，会把里面所有安装包都当补丁，噪音极大。
        // v2 起改用本程序自己的 DropFolder，需要用户主动把补丁放进去。
        if (ConfigVersion < 2)
        {
            var systemDownloads = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");

            var isSystemDownloads =
                string.IsNullOrWhiteSpace(DownloadFolder) ||
                string.Equals(DownloadFolder.TrimEnd('\\'), systemDownloads.TrimEnd('\\'),
                    StringComparison.OrdinalIgnoreCase);

            if (isSystemDownloads)
            {
                DownloadFolder = DefaultDropFolder;
                Services.Log.Info($"配置迁移：监控目录从系统下载目录改为 {DownloadFolder}");
            }

            ConfigVersion = CurrentConfigVersion;
        }

        if (string.IsNullOrWhiteSpace(DownloadFolder))
        {
            DownloadFolder = DefaultDropFolder;
        }

        if (string.IsNullOrWhiteSpace(InstalledArchiveFolder))
        {
            InstalledArchiveFolder = Path.Combine(appDataRoot, "InstalledPatches");
        }

        if (PollIntervalSeconds < 1) PollIntervalSeconds = 1;
        if (PollIntervalSeconds > 60) PollIntervalSeconds = 60;
        if (StableChecksRequired < 1) StableChecksRequired = 1;
        if (StableChecksRequired > 30) StableChecksRequired = 30;
        if (MinQuietSeconds < 0) MinQuietSeconds = 0;
        if (MinQuietSeconds > 60) MinQuietSeconds = 60;
        if (MinFileSizeMB < 0) MinFileSizeMB = 0;
        if (MinFileSizeMB > 10240) MinFileSizeMB = 10240;

        if (!new[] { "Auto", "Direct", "SystemProxy", "CustomProxy" }.Contains(NetworkMode))
        {
            NetworkMode = "Auto";
        }

        if (MetadataConcurrency < 1) MetadataConcurrency = 1;
        if (MetadataConcurrency > 16) MetadataConcurrency = 16;
        if (SearchResultLimit < 1) SearchResultLimit = 1;
        if (SearchResultLimit > 500) SearchResultLimit = 500;
        if (HttpTimeoutSeconds < 5) HttpTimeoutSeconds = 5;
        if (HttpTimeoutSeconds > 120) HttpTimeoutSeconds = 120;

        // 规范化扩展名，避免用户输入 ".ZIP" 之类导致匹配失败
        IgnoredExtensions = IgnoredExtensions
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x.Trim().TrimStart('.').ToLowerInvariant())
            .Distinct()
            .ToList();

        PatchExtensions = PatchExtensions
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x.Trim().TrimStart('.').ToLowerInvariant())
            .Distinct()
            .ToList();

        if (IgnoredExtensions.Count == 0)
        {
            IgnoredExtensions.AddRange(new[] { "crdownload", "part", "tmp", "partial" });
        }

        if (PatchExtensions.Count == 0)
        {
            PatchExtensions.AddRange(new[] { "zip", "7z", "rar", "exe" });
        }

        SearchProvider = (SearchProvider ?? "Browser").Trim();

        if (!new[] { "Browser", "Searxng", "CustomJson" }.Contains(SearchProvider))
        {
            SearchProvider = "Browser";
        }

        if (!new[] { "None", "Header", "Query" }.Contains(CustomSearchApiKeyMode))
        {
            CustomSearchApiKeyMode = "None";
        }
    }
}

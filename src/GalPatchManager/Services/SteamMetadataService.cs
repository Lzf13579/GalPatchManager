using System.Net;
using System.Text.Json;
using GalPatchManager.Models;

namespace GalPatchManager.Services;

/// <summary>某个 AppID 的商店元数据。</summary>
public sealed class SteamAppMetadata
{
    public string AppId { get; set; } = "";

    public string Name { get; set; } = "";

    /// <summary>官方类型（genres），例如 Action / Adventure / RPG。</summary>
    public List<string> Genres { get; set; } = new();

    public List<string> Categories { get; set; } = new();

    /// <summary>官方支持语言原文。</summary>
    public string SupportedLanguages { get; set; } = "";

    public List<string> Developers { get; set; } = new();

    public List<string> Publishers { get; set; } = new();

    public string ReleaseDate { get; set; } = "";

    /// <summary>抓取时间，用于过期判断。</summary>
    public DateTime FetchedAt { get; set; } = DateTime.Now;

    /// <summary>
    /// 是否原生支持中文（简体或繁体）。
    /// 实现委托给 <see cref="LibraryStatus.DetectChinese"/>，避免两处逻辑不一致。
    /// </summary>
    public bool SupportsChinese => LibraryStatus.DetectChinese(SupportedLanguages);
}

/// <summary>
/// Steam 商店元数据抓取（官方公开接口）。
///
/// 关于"标签"的重要说明：
///   * Steam 官方接口只提供 **genres（类型）**，即 Action / Adventure / RPG / Indie 这类，
///     **不提供社区用户标签**（Visual Novel / Anime / Gore 这些是社区标签，没有官方 API）。
///   * 因此本程序按"能拿到的数据"工作：官方 genres + 本地引擎特征，
///     不会声称能读取用户标签。
///   * 接口无需 API Key。抓取结果缓存到本地，默认 14 天内不重复请求。
/// </summary>
public static class SteamMetadataService
{
    private static readonly HttpClient DirectHttp = CreateClient(useProxy: false, proxy: null);

    private static readonly HttpClient SystemProxyHttp =
        CreateClient(useProxy: true, proxy: WebRequest.GetSystemWebProxy());

    private static readonly object Gate = new();

    /// <summary>已经验证可用的策略（避免每次都重新试探）。null = 还没定。</summary>
    private static string? _chosenStrategy;

    /// <summary>是否已经探测过，但全部失败（避免对每个游戏都重复超时）。</summary>
    private static bool _allFailed;

    private static HttpClient CreateClient(bool useProxy, IWebProxy? proxy)
    {
        var handler = new HttpClientHandler
        {
            UseProxy = useProxy,
            AllowAutoRedirect = true,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
            UseCookies = false,
        };

        if (useProxy && proxy is not null) handler.Proxy = proxy;

        var client = new HttpClient(handler)
        {
            // 网络不通时快速失败，避免一次归类卡几分钟
            Timeout = TimeSpan.FromSeconds(8),
        };

        client.DefaultRequestHeaders.TryAddWithoutValidation(
            "User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) GalPatchManager/1.0");

        return client;
    }

    /// <summary>
    /// 根据网络模式给出候选策略（按尝试顺序）。
    ///
    /// 说明：很多用户装了代理软件（Clash 等）并把系统代理指向本地端口，
    /// 但这类代理对某些站点的 TLS 处理可能失败；此时**直连往往反而可用**。
    /// 所以 Auto 模式先直连、再退到系统代理。
    /// </summary>
    private static List<(string Name, HttpClient Client)> BuildCandidates(string networkMode, string customProxyUrl)
    {
        var list = new List<(string, HttpClient)>();

        void AddDirect() => list.Add(("直连", DirectHttp));

        void AddSystem()
        {
            if (!list.Any(x => x.Item1 == "系统代理")) list.Add(("系统代理", SystemProxyHttp));
        }

        switch ((networkMode ?? "Auto").Trim())
        {
            case "Direct":
                AddDirect();
                break;

            case "SystemProxy":
                AddSystem();
                break;

            case "CustomProxy":
                var custom = CreateCustomClient(customProxyUrl);

                if (custom is not null) list.Add(("自定义代理", custom));
                else AddDirect(); // 地址无效时退回直连，别让功能整个瘫痪
                break;

            default: // Auto
                AddDirect();
                AddSystem();
                AddCustom(customProxyUrl, list);
                break;
        }

        return list;
    }

    private static void AddCustom(string customProxyUrl, List<(string, HttpClient)> list)
    {
        var custom = CreateCustomClient(customProxyUrl);

        if (custom is not null) list.Add(("自定义代理", custom));
    }

    private static HttpClient? CreateCustomClient(string customProxyUrl)
    {
        if (string.IsNullOrWhiteSpace(customProxyUrl)) return null;

        try
        {
            if (!Uri.TryCreate(customProxyUrl.Trim(), UriKind.Absolute, out var uri)) return null;

            if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) return null;

            return CreateClient(useProxy: true, proxy: new WebProxy(uri));
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 探测网络是否可用（只请求一次）。
    /// 返回可用的策略名；全部失败返回 null。
    /// </summary>
    public static async Task<string?> ProbeAsync(
        AppSettings settings,
        CancellationToken cancellationToken = default)
    {
        lock (Gate)
        {
            if (_chosenStrategy is not null) return _chosenStrategy;
            if (_allFailed && settings.NetworkMode == "Auto") return null;
        }

        var candidates = BuildCandidates(settings.NetworkMode, settings.CustomProxyUrl);

        foreach (var (name, client) in candidates)
        {
            var ok = await TryOnceAsync(client, "2845270", cancellationToken).ConfigureAwait(false);

            if (ok is not null)
            {
                lock (Gate) _chosenStrategy = name;
                Log.Info($"Steam 元数据网络策略可用: {name}");
                return name;
            }
        }

        lock (Gate) _allFailed = true;

        Log.Warn("所有网络策略都无法访问 Steam 商店接口（已改为仅使用本地识别）");
        return null;
    }

    /// <summary>探测结果（用于界面提示）。</summary>
    public static string? ChosenStrategy
    {
        get { lock (Gate) return _chosenStrategy; }
    }

    public static bool AllFailed
    {
        get { lock (Gate) return _allFailed; }
    }

    /// <summary>重置探测状态（用户改设置后调用）。</summary>
    public static void ResetProbe()
    {
        lock (Gate)
        {
            _chosenStrategy = null;
            _allFailed = false;
        }
    }

    /// <summary>用指定客户端尝试抓一次；成功返回元数据。</summary>
    private static async Task<SteamAppMetadata?> TryOnceAsync(
        HttpClient client,
        string appId,
        CancellationToken cancellationToken)
    {
        try
        {
            var url = $"https://store.steampowered.com/api/appdetails?appids={Uri.EscapeDataString(appId)}&l=english";

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

            using var resp = await client.GetAsync(url, cts.Token).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();

            var json = await resp.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);

            return Parse(appId, json);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null; // 超时
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 抓取单个 AppID 的官方元数据。
    /// 会按网络模式自动选择可用策略；全部失败返回 null。
    /// </summary>
    public static async Task<SteamAppMetadata?> FetchAsync(
        string appId,
        AppSettings settings,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(appId)) return null;

        var strategy = await ProbeAsync(settings, cancellationToken).ConfigureAwait(false);

        if (strategy is null) return null;

        var candidates = BuildCandidates(settings.NetworkMode, settings.CustomProxyUrl);
        var chosen = candidates.FirstOrDefault(c => c.Name == strategy);

        var client = chosen.Client ?? DirectHttp;

        var result = await TryOnceAsync(client, appId, cancellationToken).ConfigureAwait(false);

        if (result is not null) return result;

        // 已选策略这次没成功，可能网络抖动：重新探测一次
        lock (Gate) _chosenStrategy = null;

        var retryStrategy = await ProbeAsync(settings, cancellationToken).ConfigureAwait(false);

        if (retryStrategy is null) return null;

        var retryClient = BuildCandidates(settings.NetworkMode, settings.CustomProxyUrl)
            .FirstOrDefault(c => c.Name == retryStrategy).Client ?? DirectHttp;

        return await TryOnceAsync(retryClient, appId, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>按策略名挑一个 HttpClient。</summary>
    private static HttpClient SelectClient(AppSettings settings, string strategy)
    {
        var candidates = BuildCandidates(settings.NetworkMode, settings.CustomProxyUrl);

        return candidates.FirstOrDefault(c => c.Name == strategy).Client ?? DirectHttp;
    }

    /// <summary>
    /// 用「先探测、按策略请求」的通用流程抓一段 URL 文本。
    /// 供社区标签抓取复用，保证代理策略与失败处理一致。
    /// </summary>
    public static async Task<string?> FetchTextAsync(
        string url,
        AppSettings settings,
        int maxAttempts,
        CancellationToken cancellationToken = default)
    {
        var strategy = await ProbeAsync(settings, cancellationToken).ConfigureAwait(false);

        if (strategy is null) return null;

        var client = SelectClient(settings, strategy);

        try
        {
            return await Retrier.RunAsync(
                async () =>
                {
                    using var resp = await client.GetAsync(url, cancellationToken).ConfigureAwait(false);
                    resp.EnsureSuccessStatusCode();
                    return await resp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                },
                maxAttempts: Math.Max(1, maxAttempts),
                baseDelayMs: 400,
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Warn($"抓取页面失败: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// 抓取某游戏的 **Steam 社区标签 ID**（Visual Novel / Anime / Hentai 这类）。
    ///
    /// 为什么不用 appdetails：官方接口只给 genres，没有社区标签。
    /// 实测确认商店页标签是 JS 异步渲染的，所以这里改查搜索接口
    /// <c>/search/results/?json=1&amp;term=&lt;关键词&gt;</c>，
    /// 返回的每一项都带 <c>data-ds-tagids</c>，即该游戏的标签 ID 列表。
    ///
    /// 尝试顺序（前者不中再退到后者）：
    ///   1. 用**游戏名**搜索 —— 最精确；
    ///   2. 用 **AppID** 搜索 —— 名字特殊/带符号/多语言时，名字可能搜不到，
    ///      但 AppID 搜索往往仍能命中；
    ///   3. 抓**商店页 HTML** —— 兜底（实测标签是 JS 渲染的，通常取不到）。
    /// </summary>
    public static async Task<List<int>> FetchCommunityTagIdsAsync(
        string appId,
        string gameName,
        AppSettings settings,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(appId)) return new List<int>();

        // ---- 1) 按游戏名 ----
        if (!string.IsNullOrWhiteSpace(gameName))
        {
            var byName = await SearchTagIdsAsync(appId, gameName, settings, cancellationToken)
                .ConfigureAwait(false);

            if (byName.Count > 0) return byName;
        }

        // ---- 2) 按 AppID ----
        var byId = await SearchTagIdsAsync(appId, appId, settings, cancellationToken)
            .ConfigureAwait(false);

        if (byId.Count > 0) return byId;

        // ---- 3) 商店页兜底 ----
        try
        {
            var html = await FetchTextAsync(
                $"https://store.steampowered.com/app/{Uri.EscapeDataString(appId)}/?cc=us&l=english",
                settings,
                maxAttempts: 1,
                cancellationToken).ConfigureAwait(false);

            if (!string.IsNullOrWhiteSpace(html))
            {
                var fromHtml = ParseTagIdsFromHtml(html);

                if (fromHtml.Count > 0)
                {
                    Log.Info($"社区标签来自商店页兜底: appid={appId}");
                    return fromHtml;
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Debug($"商店页兜底抓标签失败 appid={appId}: {ex.Message}");
        }

        return new List<int>();
    }

    /// <summary>用某个关键词查搜索接口，取出指定 AppID 的标签 ID。</summary>
    private static async Task<List<int>> SearchTagIdsAsync(
        string appId,
        string term,
        AppSettings settings,
        CancellationToken cancellationToken)
    {
        var url = "https://store.steampowered.com/search/results/?json=1&infinite=1&start=0&count=20"
                  + $"&term={Uri.EscapeDataString(term)}&cc=us&l=english";

        try
        {
            var json = await FetchTextAsync(url, settings, maxAttempts: 1, cancellationToken)
                .ConfigureAwait(false);

            if (string.IsNullOrWhiteSpace(json)) return new List<int>();

            return ParseTagIdsForApp(json, appId);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Debug($"按「{term}」搜索标签失败 appid={appId}: {ex.Message}");
            return new List<int>();
        }
    }

    /// <summary>
    /// 从搜索返回的 JSON 里找出指定 AppID 那一项的 <c>data-ds-tagids</c>。
    /// 纯离线解析，自检可直接用样本字符串测。
    ///
    /// 注意：搜索接口的 <c>results_html</c> 是 **JSON 转义过的 HTML**，
    /// 属性写成 <c>data-ds-appid=\"1144400\"</c>（带反斜杠）。
    /// 所以匹配前必须先反转义，否则正则永远命中不了 —— 这是踩过的坑。
    /// </summary>
    public static List<int> ParseTagIdsForApp(string searchJson, string appId)
    {
        var result = new List<int>();

        if (string.IsNullOrWhiteSpace(searchJson) || string.IsNullOrWhiteSpace(appId)) return result;

        var html = UnescapeJsonHtml(searchJson);

        // 形如：data-ds-appid="1144400"  ...  data-ds-tagids="[3799,9551,...]"
        // 用 [\s\S]{0,400}? 允许两个属性之间存在换行。
        var pattern = "data-ds-appid=\""
                      + System.Text.RegularExpressions.Regex.Escape(appId.Trim())
                      + "\"[\\s\\S]{0,400}?data-ds-tagids=\"\\[([0-9,\\s]*)\\]\"";

        var m = System.Text.RegularExpressions.Regex.Match(
            html, pattern, System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        if (!m.Success || m.Groups.Count < 2) return result;

        foreach (var part in m.Groups[1].Value.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            if (int.TryParse(part.Trim(), out var id)) result.Add(id);
        }

        return result;
    }

    /// <summary>
    /// 把 JSON 里转义过的 HTML 反转义成可正则匹配的文本。
    /// 对未转义的输入是幂等的。
    /// </summary>
    public static string UnescapeJsonHtml(string text)
    {
        if (string.IsNullOrEmpty(text)) return "";

        return text
            .Replace("\\\"", "\"")
            .Replace("\\/", "/")
            .Replace("\\n", "\n")
            .Replace("\\r", "\r")
            .Replace("\\t", "\t")
            .Replace("\\\\", "\\");
    }

    /// <summary>从商店页 HTML 里抽取 <c>data-ds-tagids</c>（备用路径，通常取不到）。</summary>
    public static List<int> ParseTagIdsFromHtml(string html)
    {
        var result = new List<int>();

        if (string.IsNullOrWhiteSpace(html)) return result;

        var m = System.Text.RegularExpressions.Regex.Match(
            html, "data-ds-tagids=\"\\[([0-9,\\s]*)\\]\"",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        if (!m.Success) return result;

        foreach (var part in m.Groups[1].Value.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            if (int.TryParse(part.Trim(), out var id)) result.Add(id);
        }

        return result;
    }

    /// <summary>解析 appdetails 返回的 JSON（纯离线，自检也用它）。</summary>
    public static SteamAppMetadata? Parse(string appId, string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);

            if (!doc.RootElement.TryGetProperty(appId, out var node)) return null;

            if (!node.TryGetProperty("success", out var success) || success.ValueKind != JsonValueKind.True)
            {
                return null;
            }

            if (!node.TryGetProperty("data", out var data)) return null;

            var meta = new SteamAppMetadata { AppId = appId };

            if (data.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String)
            {
                meta.Name = name.GetString() ?? "";
            }

            if (data.TryGetProperty("genres", out var genres) && genres.ValueKind == JsonValueKind.Array)
            {
                foreach (var g in genres.EnumerateArray())
                {
                    if (g.TryGetProperty("description", out var d) && d.ValueKind == JsonValueKind.String)
                    {
                        var v = d.GetString();
                        if (!string.IsNullOrWhiteSpace(v)) meta.Genres.Add(v!);
                    }
                }
            }

            if (data.TryGetProperty("categories", out var cats) && cats.ValueKind == JsonValueKind.Array)
            {
                foreach (var c in cats.EnumerateArray())
                {
                    if (c.TryGetProperty("description", out var d) && d.ValueKind == JsonValueKind.String)
                    {
                        var v = d.GetString();
                        if (!string.IsNullOrWhiteSpace(v)) meta.Categories.Add(v!);
                    }
                }
            }

            // 支持语言（用于判断是否原生支持中文）
            if (data.TryGetProperty("supported_languages", out var langs) && langs.ValueKind == JsonValueKind.String)
            {
                meta.SupportedLanguages = StripHtml(langs.GetString() ?? "");
            }

            meta.Developers = ReadStringArray(data, "developers");
            meta.Publishers = ReadStringArray(data, "publishers");

            if (data.TryGetProperty("release_date", out var rd) &&
                rd.ValueKind == JsonValueKind.Object &&
                rd.TryGetProperty("date", out var dateVal) &&
                dateVal.ValueKind == JsonValueKind.String)
            {
                meta.ReleaseDate = dateVal.GetString() ?? "";
            }

            return meta;
        }
        catch (Exception ex)
        {
            Log.Warn($"解析 Steam 元数据失败 appid={appId}: {ex.Message}");
            return null;
        }
    }

    /// <summary>读取字符串数组字段。</summary>
    private static List<string> ReadStringArray(JsonElement data, string property)
    {
        var list = new List<string>();

        if (data.TryGetProperty(property, out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in arr.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String)
                {
                    var v = item.GetString();
                    if (!string.IsNullOrWhiteSpace(v)) list.Add(v!);
                }
            }
        }

        return list;
    }

    /// <summary>去掉语言字段里夹带的 HTML 标签。</summary>
    public static string StripHtml(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";

        var text = System.Text.RegularExpressions.Regex.Replace(value, "<[^>]+>", " ");
        text = System.Net.WebUtility.HtmlDecode(text);
        text = System.Text.RegularExpressions.Regex.Replace(text, @"\s+", " ");

        return text.Trim();
    }

    /// <summary>
    /// 把官方 genres 按映射规则折算成本程序的游戏类型。
    /// 多个 genre 命中时，取映射表里**最靠前**的那个（配置顺序即优先级）。
    /// </summary>
    public static string? MapToGameType(
        IEnumerable<string> genres,
        Dictionary<string, string> mapping,
        IReadOnlyList<string> knownTypes)
    {
        var list = genres.Where(g => !string.IsNullOrWhiteSpace(g)).ToList();

        if (list.Count == 0) return null;

        // 需要的是"按用户配置的优先顺序"，所以遍历映射表自身
        foreach (var kv in mapping)
        {
            if (string.IsNullOrWhiteSpace(kv.Key) || string.IsNullOrWhiteSpace(kv.Value)) continue;

            if (list.Any(g => string.Equals(g.Trim(), kv.Key.Trim(), StringComparison.OrdinalIgnoreCase)))
            {
                var type = kv.Value.Trim();

                // 只接受已知类型，避免把菜单撑爆
                if (knownTypes.Count == 0 || knownTypes.Contains(type, StringComparer.OrdinalIgnoreCase))
                {
                    return type;
                }
            }
        }

        return null;
    }

    /// <summary>缓存是否过期。</summary>
    public static bool IsStale(DateTime fetchedAt, int maxAgeDays) =>
        (DateTime.Now - fetchedAt).TotalDays > Math.Max(1, maxAgeDays);
}

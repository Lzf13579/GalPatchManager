using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Web;
using GalPatchManager.Models;

namespace GalPatchManager.Services;

/// <summary>一次搜索的返回结果。</summary>
public sealed class SearchOutcome
{
    public List<PatchCandidate> Candidates { get; } = new();

    /// <summary>需要用户用浏览器打开的搜索地址（无 API Key 时的备用方案）。</summary>
    public List<string> BrowserUrls { get; } = new();

    public string Message { get; set; } = "";

    public bool IsError { get; set; }

    /// <summary>是否使用了浏览器备用方案。</summary>
    public bool UsedBrowserFallback { get; set; }
}

/// <summary>
/// 补丁网址搜索。
///
/// 设计约束（重要）：
///   * 搜索结果只是**候选网址**，程序不会把网页链接当成已验证的补丁文件。
///   * 没有配置搜索服务时，绝不伪造结果，只提供浏览器搜索入口。
///   * CustomJson 接口只允许 http/https，且拒绝环回/内网/链路本地地址，避免被用作 SSRF 跳板。
///     （SearXNG 显式允许 localhost，因为自建实例通常在本机。）
/// </summary>
public sealed class SearchService
{
    private readonly AppSettings _settings;

    private static readonly HttpClient Http = CreateClient();

    public SearchService(AppSettings settings)
    {
        _settings = settings;
    }

    private static HttpClient CreateClient()
    {
        var handler = new HttpClientHandler
        {
            AllowAutoRedirect = true,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
            UseCookies = false,
        };

        var client = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(30),
        };

        client.DefaultRequestHeaders.TryAddWithoutValidation(
            "User-Agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) GalPatchManager/1.0");

        client.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "application/json, text/plain, */*");

        return client;
    }

    /// <summary>根据设置生成搜索关键词。</summary>
    public List<string> BuildQueries(string gameName)
    {
        var name = (gameName ?? "").Trim();
        var list = new List<string>();

        if (name.Length == 0) return list;

        foreach (var template in _settings.SearchKeywordTemplates)
        {
            if (string.IsNullOrWhiteSpace(template)) continue;

            var q = template.Contains("{0}", StringComparison.Ordinal)
                ? template.Replace("{0}", name, StringComparison.Ordinal)
                : $"{name} {template}";

            q = q.Trim();

            if (q.Length > 0 && !list.Contains(q, StringComparer.Ordinal)) list.Add(q);
        }

        return list;
    }

    /// <summary>生成“用浏览器搜索”的地址（备用方案，始终可用）。</summary>
    public List<string> BuildBrowserUrls(IEnumerable<string> queries)
    {
        var urls = new List<string>();

        foreach (var q in queries)
        {
            var encoded = HttpUtility.UrlEncode(q);

            urls.Add($"https://www.bing.com/search?q={encoded}");
            urls.Add($"https://www.google.com/search?q={encoded}");
            urls.Add($"https://search.bilibili.com/all?keyword={encoded}");
            urls.Add($"https://www.baidu.com/s?wd={encoded}");
        }

        return urls;
    }

    /// <summary>执行搜索。</summary>
    public async Task<SearchOutcome> SearchAsync(string gameName, CancellationToken cancellationToken)
    {
        var outcome = new SearchOutcome();
        var queries = BuildQueries(gameName);

        if (queries.Count == 0)
        {
            outcome.IsError = true;
            outcome.Message = "请先输入游戏名称。";
            return outcome;
        }

        var provider = _settings.SearchProvider;

        // ---- 浏览器方案：不发任何请求，只给出搜索链接 ----
        if (string.Equals(provider, "Browser", StringComparison.OrdinalIgnoreCase))
        {
            outcome.BrowserUrls.AddRange(BuildBrowserUrls(queries));
            outcome.UsedBrowserFallback = true;
            outcome.Message = "当前使用“浏览器搜索”方式：程序不会伪造搜索结果，"
                            + "请点击下方链接在浏览器中确认可用网址后，把可信网址或域名加入白名单。";
            return outcome;
        }

        try
        {
            switch (provider)
            {
                case "Searxng":
                    await SearchSearxngAsync(queries, outcome, cancellationToken).ConfigureAwait(false);
                    break;

                case "CustomJson":
                    await SearchCustomJsonAsync(queries, outcome, cancellationToken).ConfigureAwait(false);
                    break;

                default:
                    outcome.BrowserUrls.AddRange(BuildBrowserUrls(queries));
                    outcome.UsedBrowserFallback = true;
                    outcome.Message = $"未知的搜索方式「{provider}」，已回退为浏览器搜索。";
                    return outcome;
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Error($"搜索失败: {provider}", ex);

            // 任何失败都回退到浏览器方案，而不是给出假结果
            outcome.BrowserUrls.AddRange(BuildBrowserUrls(queries));
            outcome.UsedBrowserFallback = true;
            outcome.IsError = true;
            outcome.Message = $"搜索接口调用失败：{ex.Message}\r\n已为你准备好浏览器搜索链接作为备用方案。";
            return outcome;
        }

        if (outcome.Candidates.Count == 0)
        {
            if (!outcome.IsError)
            {
                outcome.Message = "搜索服务没有返回任何结果。";
            }

            outcome.BrowserUrls.AddRange(BuildBrowserUrls(queries));
            outcome.UsedBrowserFallback = true;
        }

        return outcome;
    }

    // ------------------------------------------------------------------ SearXNG

    private async Task SearchSearxngAsync(
        List<string> queries, SearchOutcome outcome, CancellationToken cancellationToken)
    {
        var baseUrl = (_settings.SearxngBaseUrl ?? "").Trim();

        if (baseUrl.Length == 0)
        {
            outcome.IsError = true;
            outcome.Message = "尚未配置 SearXNG 实例地址。请在“设置”中填写，例如 http://127.0.0.1:8888 。";
            return;
        }

        if (!TryBuildUri(baseUrl, out var baseUri))
        {
            outcome.IsError = true;
            outcome.Message = "SearXNG 地址无效。";
            return;
        }

        foreach (var query in queries)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var url = baseUri!.ToString().TrimEnd('/')
                      + "/search?q=" + HttpUtility.UrlEncode(query) + "&format=json";

            var body = await Retrier.RunAsync(
                async () =>
                {
                    using var resp = await Http.GetAsync(url, cancellationToken).ConfigureAwait(false);
                    resp.EnsureSuccessStatusCode();

                    var mediaType = resp.Content.Headers.ContentType?.MediaType ?? "";

                    if (mediaType.Contains("html", StringComparison.OrdinalIgnoreCase))
                    {
                        throw new InvalidOperationException(
                            "SearXNG 返回了 HTML 而不是 JSON。请在实例的 settings.yml 中开启 json 格式输出。");
                    }

                    return await resp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                },
                cancellationToken: cancellationToken).ConfigureAwait(false);

            ParseSearxngJson(body, query, outcome);
            await Task.Delay(250, cancellationToken).ConfigureAwait(false);
        }
    }

    private void ParseSearxngJson(string json, string query, SearchOutcome outcome)
    {
        using var doc = JsonDocument.Parse(json);

        if (!doc.RootElement.TryGetProperty("results", out var results) ||
            results.ValueKind != JsonValueKind.Array)
        {
            outcome.IsError = true;
            outcome.Message = "SearXNG 返回的数据结构中没有 results 数组。";
            return;
        }

        foreach (var item in results.EnumerateArray())
        {
            var url = GetString(item, "url");
            if (string.IsNullOrWhiteSpace(url)) continue;

            outcome.Candidates.Add(new PatchCandidate
            {
                Title = WebUtility.HtmlDecode(GetString(item, "title") ?? url),
                Url = url!,
                Snippet = WebUtility.HtmlDecode(GetString(item, "content") ?? ""),
                Engine = "SearXNG",
            });
        }
    }

    // -------------------------------------------------------------- Custom JSON

    private async Task SearchCustomJsonAsync(
        List<string> queries, SearchOutcome outcome, CancellationToken cancellationToken)
    {
        var template = (_settings.CustomSearchUrlTemplate ?? "").Trim();

        if (template.Length == 0 || !template.Contains("{query}", StringComparison.OrdinalIgnoreCase))
        {
            outcome.IsError = true;
            outcome.Message = "尚未配置自定义搜索接口，或 URL 模板中缺少 {query} 占位符。";
            return;
        }

        foreach (var query in queries)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var url = template.Replace("{query}", HttpUtility.UrlEncode(query), StringComparison.OrdinalIgnoreCase);

            if (!TryBuildUri(url, out var uri))
            {
                outcome.IsError = true;
                outcome.Message = $"搜索地址无效: {url}";
                return;
            }

            if (!await NetworkGuard.IsPublicEndpointAsync(uri!, allowLoopback: false, cancellationToken)
                    .ConfigureAwait(false))
            {
                outcome.IsError = true;
                outcome.Message = "自定义搜索地址解析到本机或内网地址，已拒绝访问（安全限制）。"
                                + "如果你需要访问内网服务，请改用 SearXNG 方式。";
                return;
            }

            using var request = new HttpRequestMessage(HttpMethod.Get, uri);

            if (!string.IsNullOrWhiteSpace(_settings.CustomSearchApiKey))
            {
                switch (_settings.CustomSearchApiKeyMode)
                {
                    case "Header":
                        var headerName = string.IsNullOrWhiteSpace(_settings.CustomSearchApiKeyName)
                            ? "Authorization"
                            : _settings.CustomSearchApiKeyName.Trim();

                        if (headerName.Equals("Authorization", StringComparison.OrdinalIgnoreCase) &&
                            !_settings.CustomSearchApiKey.StartsWith("Bearer", StringComparison.OrdinalIgnoreCase))
                        {
                            request.Headers.TryAddWithoutValidation(
                                headerName, "Bearer " + _settings.CustomSearchApiKey);
                        }
                        else
                        {
                            request.Headers.TryAddWithoutValidation(headerName, _settings.CustomSearchApiKey);
                        }
                        break;

                    case "Query":
                        // 已在 URL 模板外的补充参数
                        var paramName = string.IsNullOrWhiteSpace(_settings.CustomSearchApiKeyName)
                            ? "api_key"
                            : _settings.CustomSearchApiKeyName.Trim();

                        var builder = new UriBuilder(uri!);
                        var existing = builder.Query.TrimStart('?');
                        var extra = $"{HttpUtility.UrlEncode(paramName)}={HttpUtility.UrlEncode(_settings.CustomSearchApiKey)}";
                        builder.Query = existing.Length == 0 ? extra : existing + "&" + extra;
                        request.RequestUri = builder.Uri;
                        break;
                }
            }

            var body = await Retrier.RunAsync(
                async () =>
                {
                    using var resp = await Http.SendAsync(request, cancellationToken).ConfigureAwait(false);
                    resp.EnsureSuccessStatusCode();
                    return await resp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                },
                cancellationToken: cancellationToken).ConfigureAwait(false);

            ParseCustomJson(body, query, outcome);
            await Task.Delay(250, cancellationToken).ConfigureAwait(false);
        }
    }

    private void ParseCustomJson(string json, string query, SearchOutcome outcome)
    {
        using var doc = JsonDocument.Parse(json);

        JsonElement array = default;

        // 1) 用户指定路径，形如 "data.items"
        if (!string.IsNullOrWhiteSpace(_settings.CustomSearchResultsPath) &&
            TryNavigate(doc.RootElement, _settings.CustomSearchResultsPath.Trim(), out var configured))
        {
            array = configured;
        }

        // 2) 自动探测常见字段
        if (array.ValueKind != JsonValueKind.Array)
        {
            foreach (var key in new[] { "results", "items", "data", "organic_results", "web", "hits" })
            {
                if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                    doc.RootElement.TryGetProperty(key, out var candidate) &&
                    candidate.ValueKind == JsonValueKind.Array)
                {
                    array = candidate;
                    break;
                }
            }
        }

        if (array.ValueKind != JsonValueKind.Array)
        {
            outcome.IsError = true;
            outcome.Message = "无法在返回的 JSON 中定位结果数组。可在“设置”中指定结果数组路径，例如 data.items。";
            return;
        }

        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;

            var url = FirstString(item, "url", "link", "href", "web_url");
            if (string.IsNullOrWhiteSpace(url)) continue;

            outcome.Candidates.Add(new PatchCandidate
            {
                Title = WebUtility.HtmlDecode(
                    FirstString(item, "title", "name", "headline") ?? url!),
                Url = url!,
                Snippet = WebUtility.HtmlDecode(
                    FirstString(item, "snippet", "description", "content", "summary", "text") ?? ""),
                Engine = "自定义接口",
            });
        }

        _ = query;
    }

    // ------------------------------------------------------------------- 工具

    /// <summary>标注白名单并去重、限量、补充站点名。</summary>
    public void FinalizeCandidates(SearchOutcome outcome)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var kept = new List<PatchCandidate>();

        foreach (var c in outcome.Candidates)
        {
            if (string.IsNullOrWhiteSpace(c.Url)) continue;
            if (!seen.Add(c.Url)) continue;

            if (!Uri.TryCreate(c.Url, UriKind.Absolute, out var uri)) continue;

            c.Site = uri.Host;
            c.IsWhitelisted = IsWhitelisted(uri);

            kept.Add(c);

            if (kept.Count >= _settings.SearchResultLimit) break;
        }

        outcome.Candidates.Clear();
        outcome.Candidates.AddRange(kept);
    }

    private bool IsWhitelisted(Uri uri)
    {
        var host = uri.Host;

        if (_settings.WhitelistedUrls.Any(u =>
                !string.IsNullOrWhiteSpace(u) &&
                uri.AbsoluteUri.StartsWith(u.Trim(), StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        foreach (var rule in _settings.WhitelistedHosts)
        {
            if (string.IsNullOrWhiteSpace(rule)) continue;

            var r = rule.Trim().TrimStart('.');

            // 支持 example.com 与 *.example.com
            if (host.Equals(r, StringComparison.OrdinalIgnoreCase) ||
                host.EndsWith("." + r, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static string? GetString(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    private static string? FirstString(JsonElement element, params string[] names)
    {
        foreach (var n in names)
        {
            var v = GetString(element, n);
            if (!string.IsNullOrWhiteSpace(v)) return v;
        }

        return null;
    }

    private static bool TryNavigate(JsonElement root, string path, out JsonElement result)
    {
        result = default;
        var current = root;

        foreach (var partRaw in path.Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            var part = partRaw.Trim();

            // 支持 items[0] 形式
            var indexMatch = Regex.Match(part, @"^(?<name>[^\[]*)\[(?<idx>\d+)\]$");

            if (indexMatch.Success)
            {
                var name = indexMatch.Groups["name"].Value;

                if (name.Length > 0)
                {
                    if (current.ValueKind != JsonValueKind.Object ||
                        !current.TryGetProperty(name, out current))
                    {
                        return false;
                    }
                }

                var idx = int.Parse(indexMatch.Groups["idx"].Value);

                if (current.ValueKind != JsonValueKind.Array || current.GetArrayLength() <= idx)
                    return false;

                current = current[idx];
                continue;
            }

            if (current.ValueKind != JsonValueKind.Object ||
                !current.TryGetProperty(part, out var next))
            {
                return false;
            }

            current = next;
        }

        result = current;
        return true;
    }

    private static bool TryBuildUri(string raw, out Uri? uri)
    {
        uri = null;

        if (!Uri.TryCreate(raw, UriKind.Absolute, out var parsed)) return false;

        if (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps) return false;

        uri = parsed;
        return true;
    }
}

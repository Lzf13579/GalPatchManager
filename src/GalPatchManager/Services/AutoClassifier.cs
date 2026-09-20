using GalPatchManager.Models;

namespace GalPatchManager.Services;

/// <summary>归类结果的统计与明细。</summary>
public sealed class ClassifyReport
{
    /// <summary>固定键 -> 分配的类型。</summary>
    public Dictionary<string, string> Assignments { get; } =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>固定键 -> 依据说明。</summary>
    public Dictionary<string, string> Reasons { get; } =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>固定键 -> genres 列表。</summary>
    public Dictionary<string, List<string>> Genres { get; } =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>固定键 -> 社区标签列表。</summary>
    public Dictionary<string, List<string>> Tags { get; } =
        new(StringComparer.OrdinalIgnoreCase);

    public int FromCache { get; set; }

    public int FromNetwork { get; set; }

    /// <summary>按 Steam 社区标签归类成功的数量。</summary>
    public int FromTag { get; set; }

    /// <summary>本次新抓取到社区标签的数量。</summary>
    public int FromTagNetwork { get; set; }

    public int FromGenre { get; set; }

    public int FromHeuristic { get; set; }

    /// <summary>按游戏名关键词归类成功的数量。</summary>
    public int FromName { get; set; }

    public int SkippedManual { get; set; }

    public int NetworkFailures { get; set; }

    public int Unclassified { get; set; }
}

/// <summary>
/// 游戏类型自动归类。
///
/// 归类优先级（从高到低）：
///   1. 用户手动指定 —— 永不覆盖；
///   2. Steam 官方 genres，按用户配置的 <see cref="AppSettings.GenreTypeMapping"/> 映射；
///   3. 本地引擎特征识别（Ren'Py / KiriKiri / RPG Maker / Unity 等）；
///   4. 都失败则保持「未分类」。
///
/// 关于"标签"（重要，不要误解）：
///   Steam 官方接口只提供 **genres**（Action/Adventure/RPG...），
///   **不提供社区用户标签**（Visual Novel/Gore/Anime 属社区标签，无官方 API）。
///   所以本功能是"按能拿到的元数据 + 本地特征"归类，不声称使用了用户标签。
/// </summary>
public sealed class AutoClassifier
{
    private readonly AppSettings _settings;

    public AutoClassifier(AppSettings settings)
    {
        _settings = settings;
    }

    /// <summary>
    /// 对一个游戏归类。返回分配的类型；null 表示无法判断（保持未分类）。
    /// </summary>
    public async Task<string?> ClassifyAsync(
        GameEntry game,
        ClassifyReport report,
        bool allowNetwork,
        CancellationToken cancellationToken = default)
    {
        var key = GameTypeCatalog.BuildStableKey(game);

        // ---- 1) 用户手动指定：绝不覆盖 ----
        if (_settings.GameTypeOverrides is not null &&
            _settings.GameTypeOverrides.TryGetValue(key, out var overridden) &&
            !string.IsNullOrWhiteSpace(overridden))
        {
            report.SkippedManual++;
            return overridden.Trim();
        }

        if (!string.IsNullOrWhiteSpace(game.CustomType))
        {
            report.SkippedManual++;
            return game.CustomType!.Trim();
        }

        var knownTypes = GameTypeCatalog.BuildTypeList(_settings).Select(t => t.Name).ToList();

        // ---- 2) 元数据：先缓存，必要时联网 ----
        GameTagInfo? info = TagCache.Get(key);

        var needsFetch = allowNetwork &&
                         _settings.EnableSteamMetadata &&
                         !string.IsNullOrWhiteSpace(game.AppId) &&
                         (info is null ||
                          // 老结构缓存缺少语言等字段，需要重新抓一次
                          info.IsLegacySchema ||
                          SteamMetadataService.IsStale(info.UpdatedAt, _settings.MetadataCacheDays));

        if (needsFetch)
        {
            var meta = await SteamMetadataService.FetchAsync(game.AppId!, _settings, cancellationToken)
                .ConfigureAwait(false);

            if (meta is not null)
            {
                info = new GameTagInfo
                {
                    Name = meta.Name,
                    Genres = meta.Genres,
                    Categories = meta.Categories,
                    SupportedLanguages = meta.SupportedLanguages,
                    Developers = meta.Developers,
                    Publishers = meta.Publishers,
                    ReleaseDate = meta.ReleaseDate,
                    UpdatedAt = DateTime.Now,
                };

                TagCache.Set(key, info);
                report.FromNetwork++;
            }
            else
            {
                report.NetworkFailures++;
            }
        }
        else if (info is not null)
        {
            report.FromCache++;
        }

        // ---- 3) 用 **社区标签** 映射（优先级最高的一层自动规则）----
        //     「视觉小说」是社区标签，官方 genres 里根本没有这一项，
        //     所以必须先看标签，否则日系 AVG 只会落到「冒险」。
        var tags = info?.CommunityTags ?? new List<string>();

        if (tags.Count == 0 && allowNetwork && _settings.EnableCommunityTags &&
            !string.IsNullOrWhiteSpace(game.AppId))
        {
            var ids = await SteamMetadataService
                .FetchCommunityTagIdsAsync(game.AppId!, game.Name, _settings, cancellationToken)
                .ConfigureAwait(false);

            if (ids.Count > 0)
            {
                tags = SteamTagCatalog.NamesOf(ids);

                // 写回缓存（保留已有字段）
                info ??= new GameTagInfo { Name = game.Name };
                info.CommunityTagIds = ids;
                info.CommunityTags = tags;

                TagCache.Set(key, info);

                report.FromTagNetwork++;
            }
        }

        if (tags.Count > 0)
        {
            report.Tags[key] = tags;

            var mapped = SteamTagCatalog.MapToGameType(tags, _settings.TagTypeMapping, knownTypes);

            if (!string.IsNullOrWhiteSpace(mapped))
            {
                report.FromTag++;
                report.Assignments[key] = mapped;
                report.Reasons[key] = $"Steam 社区标签 {string.Join("/", tags.Take(5))} → {mapped}";

                if (info is not null)
                {
                    info.Type = mapped;
                    info.Reason = report.Reasons[key];
                    info.IsAutomatic = true;
                    TagCache.Set(key, info);
                }

                return mapped;
            }
        }

        // ---- 4) 用官方 genres 映射 ----
        if (info is not null && info.Genres.Count > 0)
        {
            report.Genres[key] = info.Genres;

            var mapped = SteamMetadataService.MapToGameType(
                info.Genres, _settings.GenreTypeMapping, knownTypes);

            if (!string.IsNullOrWhiteSpace(mapped))
            {
                report.FromGenre++;
                report.Assignments[key] = mapped;
                report.Reasons[key] = $"Steam 官方类型 {string.Join("/", info.Genres)} → {mapped}";

                info.Type = mapped;
                info.Reason = report.Reasons[key];
                info.IsAutomatic = true;
                TagCache.Set(key, info);

                return mapped;
            }
        }

        // ---- 4) 按游戏名/目录名关键词（纯离线，不依赖引擎特征）----
        //     放在引擎识别之前：日文名里的「ビジュアルノベル」这类信息比
        //     "这游戏是 Unity 做的" 有区分度得多。
        if (_settings.EnableNameTypeRule)
        {
            var byName = GameTypeCatalog.DetectFromName(game.Name, game.InstallDir, _settings);

            if (byName.Type is not null)
            {
                report.FromName++;
                report.Assignments[key] = byName.Type;
                report.Reasons[key] = byName.Reason;

                if (info is not null)
                {
                    info.Type = byName.Type;
                    info.Reason = byName.Reason;
                    info.IsAutomatic = true;
                    TagCache.Set(key, info);
                }

                return byName.Type;
            }
        }

        // ---- 5) 本地引擎特征识别（最后手段）----
        //     注意：引擎只能判断"技术栈"，判断不了玩法类型。
        //     Unity / Unreal 一律归「其他」，所以必须排在上面两条之后，
        //     否则会把有明确 genres 的游戏全部盖成「其他」。
        if (_settings.EnableHeuristicType)
        {
            var detected = GameTypeCatalog.DetectFromDirectory(game.InstallDir, cancellationToken);

            if (detected.Type is not null)
            {
                report.FromHeuristic++;
                report.Assignments[key] = detected.Type;
                report.Reasons[key] = detected.Reason;

                if (info is not null)
                {
                    info.Type = detected.Type;
                    info.Reason = detected.Reason;
                    info.IsAutomatic = true;
                    TagCache.Set(key, info);
                }

                return detected.Type;
            }
        }

        // ---- 6) 无法判断 ----
        report.Unclassified++;
        return null;
    }

    /// <summary>把归类结果写回游戏条目。</summary>
    public void Apply(GameEntry game, ClassifyReport report)
    {
        var key = GameTypeCatalog.BuildStableKey(game);

        if (report.Assignments.TryGetValue(key, out var type))
        {
            game.DetectedType = type;
            game.DetectedTypeReason = report.Reasons.TryGetValue(key, out var r) ? r : "";

            _settings.AutoDetectedTypes ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            _settings.AutoDetectedTypes[key] = type;
            return;
        }

        // 有 genres 但没映射上：说明原因，方便用户去设置里补映射
        if (report.Genres.TryGetValue(key, out var genres) && genres.Count > 0)
        {
            game.DetectedTypeReason =
                $"Steam 官方类型 {string.Join("/", genres)} 未匹配到映射规则，可在设置里补充";
        }
    }
}

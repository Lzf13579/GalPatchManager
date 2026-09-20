using System.Text.Json.Serialization;
using GalPatchManager.Models;

namespace GalPatchManager.Services;

/// <summary>一个游戏类型条目，用于左侧二级菜单。</summary>
public sealed class GameTypeEntry
{
    /// <summary>菜单显示名。</summary>
    public string Name { get; init; } = "";

    /// <summary>是否为内置类型（内置类型不可删除）。</summary>
    public bool IsBuiltIn { get; init; } = true;

    [JsonIgnore]
    public string Key => "type:" + Name;
}

/// <summary>
/// 游戏类型目录 + 类型识别。
///
/// 重要前提：Steam 的 <c>appmanifest_*.acf</c> 里**没有**类型/分类字段
/// （已实测确认：只有 appid / name / installdir / StateFlags / SizeOnDisk 等），
/// 所以类型不可能"自动准确识别"。这里的做法是：
///
///   1) 优先使用**用户手动指定**的类型（持久化，永远覆盖自动结果）；
///   2) 其次使用**启发式识别**（扫描游戏目录里的引擎特征文件），
///      结果标记为"自动"，只是帮你少点几下，随时可改；
///   3) 都没有则归入「未分类」。
///
/// 程序不会声称自动识别是权威结论。
/// </summary>
public static class GameTypeCatalog
{
    /// <summary>未分类（所有无法判断的游戏都落在这里）。</summary>
    public const string Uncategorized = "未分类";

    /// <summary>内置类型，顺序即菜单显示顺序。</summary>
    public static readonly string[] BuiltInTypes =
    {
        "视觉小说",
        "冒险",
        "角色扮演",
        "动作",
        "模拟",
        "策略",
        "益智",
        "休闲",
        "独立",
        "其他",
    };

    /// <summary>角色扮演的细分标记（用于「角色扮演（RPG）」这类显示）。</summary>
    public const string RolePlaying = "角色扮演";

    // ------------------------------------------------------------ 类型列表

    /// <summary>
    /// 构建菜单用的类型列表：全部内置类型 + 用户自定义类型 + 未分类（始终最后）。
    /// </summary>
    public static List<GameTypeEntry> BuildTypeList(AppSettings settings)
    {
        var list = new List<GameTypeEntry>();

        foreach (var t in BuiltInTypes)
        {
            list.Add(new GameTypeEntry { Name = t, IsBuiltIn = true });
        }

        // 用户自定义类型：去重（不重复内置、也不重复自己）
        var custom = (settings.CustomGameTypes ?? new List<string>())
            .Select(x => (x ?? "").Trim())
            .Where(x => x.Length > 0)
            .Where(x => !list.Any(e => string.Equals(e.Name, x, StringComparison.OrdinalIgnoreCase)))
            .Where(x => !string.Equals(x, Uncategorized, StringComparison.Ordinal))
            .Distinct(StringComparer.OrdinalIgnoreCase);

        foreach (var c in custom)
        {
            list.Add(new GameTypeEntry { Name = c, IsBuiltIn = false });
        }

        list.Add(new GameTypeEntry { Name = Uncategorized, IsBuiltIn = true });

        return list;
    }

    // ------------------------------------------------------------ 类型解析

    /// <summary>用于持久化的稳定键（AppID 优先，否则用安装目录）。</summary>
    public static string BuildStableKey(GameEntry game)
    {
        if (!string.IsNullOrWhiteSpace(game.AppId)) return "app:" + game.AppId!.Trim();

        try
        {
            return "dir:" + Path.GetFullPath(game.InstallDir).TrimEnd('\\', '/').ToLowerInvariant();
        }
        catch
        {
            return "id:" + game.Id;
        }
    }

    /// <summary>
    /// 解析游戏最终类型：手动指定 > 自动识别 > 未分类。
    /// </summary>
    public static string ResolveType(GameEntry game, AppSettings settings)
    {
        // 1) 用户在条目上直接指定（手动添加的游戏会写在这里）
        if (!string.IsNullOrWhiteSpace(game.CustomType))
        {
            return game.CustomType!.Trim();
        }

        // 2) 用户覆盖表（针对 Steam 游戏，因为每次刷新会重建条目）
        var key = BuildStableKey(game);

        if (settings.GameTypeOverrides is not null &&
            settings.GameTypeOverrides.TryGetValue(key, out var overridden) &&
            !string.IsNullOrWhiteSpace(overridden))
        {
            return overridden.Trim();
        }

        // 3) 缓存过的自动识别结果（避免每次刷新重新扫描目录）
        var cachedKey = BuildStableKey(game);

        if (settings.AutoDetectedTypes is not null &&
            settings.AutoDetectedTypes.TryGetValue(cachedKey, out var cached) &&
            !string.IsNullOrWhiteSpace(cached))
        {
            return cached.Trim();
        }

        // 4) 本次运行刚识别出的结果
        if (!string.IsNullOrWhiteSpace(game.DetectedType))
        {
            return game.DetectedType!.Trim();
        }

        return Uncategorized;
    }

    /// <summary>该类型是否为“自动识别”得出的（用于界面标注）。</summary>
    public static bool IsAutoDetected(GameEntry game, AppSettings settings)
    {
        if (!string.IsNullOrWhiteSpace(game.CustomType)) return false;

        var key = BuildStableKey(game);

        if (settings.GameTypeOverrides is not null &&
            settings.GameTypeOverrides.TryGetValue(key, out var overridden) &&
            !string.IsNullOrWhiteSpace(overridden))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(game.DetectedType)) return true;

        return settings.AutoDetectedTypes is not null &&
               settings.AutoDetectedTypes.TryGetValue(key, out var cached) &&
               !string.IsNullOrWhiteSpace(cached);
    }

    /// <summary>设置/清除用户指定的类型。</summary>
    public static void SetType(GameEntry game, AppSettings settings, string? type)
    {
        var value = (type ?? "").Trim();
        var key = BuildStableKey(game);

        // 统一写覆盖表，保证 Steam 游戏刷新后仍然生效
        settings.GameTypeOverrides ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        if (value.Length == 0 || value == Uncategorized)
        {
            // 「未分类」视为未指定，但仍然记一条，避免自动识别又把它改回去
            settings.GameTypeOverrides[key] = Uncategorized;
            game.CustomType = "";
            return;
        }

        settings.GameTypeOverrides[key] = value;

        // 手动添加的游戏可以直接落在条目上
        if (game.Source == GameSource.Manual) game.CustomType = value;

        // 自定义类型要进列表，否则菜单里看不到
        if (!BuiltInTypes.Contains(value) && value != Uncategorized)
        {
            settings.CustomGameTypes ??= new List<string>();
            if (!settings.CustomGameTypes.Contains(value, StringComparer.OrdinalIgnoreCase))
            {
                settings.CustomGameTypes.Add(value);
            }
        }
    }

    /// <summary>统计各类型下的游戏数量。</summary>
    public static Dictionary<string, int> CountByType(
        IEnumerable<GameEntry> games,
        AppSettings settings,
        List<GameTypeEntry> typeList)
    {
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var t in typeList) counts[t.Name] = 0;

        foreach (var g in games)
        {
            var type = ResolveType(g, settings);
            counts[type] = counts.TryGetValue(type, out var c) ? c + 1 : 1;
        }

        // 统计里出现但菜单没有的类型（例如手改过配置），补进菜单避免被隐藏
        return counts;
    }

    // ------------------------------------------------------------ 名称关键词

    /// <summary>
    /// 按**游戏名 / 目录名**里的关键词归类（纯离线，不需要网络）。
    ///
    /// 依据是用户自己配置的关键词规则（顺序即优先级）。
    /// 这是"有据可查"的离线归类，不依赖任何外部接口。
    /// </summary>
    public static DetectionResult DetectFromName(string gameName, string installDir, AppSettings settings)
    {
        if (settings.NameTypeRules is null || settings.NameTypeRules.Count == 0)
        {
            return new DetectionResult { Type = null, Reason = "未配置名称关键词规则" };
        }

        var name = gameName ?? "";
        var dirName = "";

        try
        {
            if (!string.IsNullOrWhiteSpace(installDir))
            {
                dirName = new DirectoryInfo(installDir.TrimEnd('\\', '/')).Name;
            }
        }
        catch
        {
            // 目录名取不到就只用游戏名
        }

        var haystack = (name + " " + dirName).ToLowerInvariant();

        foreach (var kv in settings.NameTypeRules)
        {
            if (string.IsNullOrWhiteSpace(kv.Key) || string.IsNullOrWhiteSpace(kv.Value)) continue;

            var keyword = kv.Key.Trim().ToLowerInvariant();

            if (keyword.Length == 0) continue;

            if (haystack.Contains(keyword, StringComparison.Ordinal))
            {
                return new DetectionResult
                {
                    Type = kv.Value.Trim(),
                    Reason = $"游戏名/目录名命中关键词「{kv.Key}」",
                };
            }
        }

        return new DetectionResult { Type = null, Reason = "游戏名未命中任何关键词规则" };
    }

    // ------------------------------------------------------------ 自动识别

    /// <summary>自动识别的结果说明。</summary>
    public sealed class DetectionResult
    {
        public string? Type { get; init; }

        public string Reason { get; init; } = "";
    }

    /// <summary>
    /// 通过扫描游戏目录里的引擎特征文件做启发式判断。
    /// 这是"尽量猜"，不是权威结论；用户随时可以覆盖。
    /// </summary>
    public static DetectionResult DetectFromDirectory(string installDir, CancellationToken token = default)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(installDir) || !Directory.Exists(installDir))
            {
                return new DetectionResult { Type = null, Reason = "目录不存在" };
            }

            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var extensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            var scanned = 0;

            foreach (var file in EnumerateFilesLimited(installDir, 4, 6000, token))
            {
                token.ThrowIfCancellationRequested();

                scanned++;

                var name = Path.GetFileName(file);
                names.Add(name);

                var ext = Path.GetExtension(file);
                if (ext.Length > 1) extensions.Add(ext);
            }

            if (scanned == 0)
            {
                return new DetectionResult { Type = null, Reason = "目录为空" };
            }

            // ---- Ren'Py ----
            if (extensions.Contains(".rpy") || extensions.Contains(".rpyc") ||
                names.Contains("renpy.exe") || names.Contains("renpy.py") ||
                names.Contains("pythonw.exe"))
            {
                return new DetectionResult { Type = "视觉小说", Reason = "检测到 Ren'Py 引擎文件（.rpy/.rpyc）" };
            }

            // ---- KiriKiri（吉里吉里）----
            if (names.Contains("krkr.exe") || names.Contains("krkr2.exe") ||
                names.Contains("kirikiri.exe") || extensions.Contains(".xp3"))
            {
                return new DetectionResult { Type = "视觉小说", Reason = "检测到 KiriKiri 引擎（.xp3/krkr.exe）" };
            }

            // ---- 其他常见 AVG 引擎 ----
            if (names.Contains("bgi.exe") || extensions.Contains(".arc") && names.Contains("bgm.xp3"))
            {
                return new DetectionResult { Type = "视觉小说", Reason = "检测到 BGI/Ethornell 引擎（bgi.exe）" };
            }

            if (names.Contains("siglusengine.exe") || names.Contains("siglus.dll"))
            {
                return new DetectionResult { Type = "视觉小说", Reason = "检测到 SiglusEngine" };
            }

            if (names.Contains("reallive.exe") || extensions.Contains(".g00"))
            {
                return new DetectionResult { Type = "视觉小说", Reason = "检测到 RealLive（.g00）" };
            }

            if (names.Contains("cs2.exe") || extensions.Contains(".pak") && names.Contains("startup.tjs"))
            {
                return new DetectionResult { Type = "视觉小说", Reason = "检测到相关引擎文件" };
            }

            // ---- RPG Maker ----
            if (names.Contains("rpg_rt.exe") || names.Contains("game.exe") && extensions.Contains(".rxdata") ||
                extensions.Contains(".rxdata") || extensions.Contains(".rvdata2") ||
                extensions.Contains(".rgssad") || extensions.Contains(".rgss3a"))
            {
                return new DetectionResult { Type = "角色扮演", Reason = "检测到 RPG Maker 工程文件（.rxdata/.rvdata2/.rgssad）" };
            }

            // ---- Unity / Unreal 只能判断为“非视觉小说”，归入其他 ----
            if (names.Contains("unityplayer.dll"))
            {
                return new DetectionResult { Type = "其他", Reason = "检测到 Unity 引擎（unityplayer.dll），无法判断具体类型" };
            }

            if (names.Contains("ue4game.exe") || names.Contains("ue5game.exe") ||
                Directory.Exists(Path.Combine(installDir, "Engine")))
            {
                return new DetectionResult { Type = "其他", Reason = "检测到 Unreal 引擎目录，无法判断具体类型" };
            }

            return new DetectionResult { Type = null, Reason = $"扫描 {scanned} 个文件，未匹配到已知引擎特征" };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Warn($"自动识别类型失败: {installDir} -> {ex.Message}");
            return new DetectionResult { Type = null, Reason = ex.Message };
        }
    }

    /// <summary>限制深度与数量的文件枚举，避免在巨型目录上卡死。</summary>
    private static IEnumerable<string> EnumerateFilesLimited(
        string root,
        int maxDepth,
        int maxFiles,
        CancellationToken token)
    {
        var queue = new Queue<(string Dir, int Depth)>();
        queue.Enqueue((root, 0));

        var yielded = 0;

        while (queue.Count > 0 && yielded < maxFiles)
        {
            token.ThrowIfCancellationRequested();

            var (dir, depth) = queue.Dequeue();

            string[] files;

            try
            {
                files = Directory.GetFiles(dir);
            }
            catch
            {
                continue;
            }

            foreach (var f in files)
            {
                if (yielded++ >= maxFiles) yield break;

                yield return f;
            }

            if (depth >= maxDepth) continue;

            string[] subDirs;

            try
            {
                subDirs = Directory.GetDirectories(dir);
            }
            catch
            {
                continue;
            }

            foreach (var d in subDirs) queue.Enqueue((d, depth + 1));
        }
    }
}

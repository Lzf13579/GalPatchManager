using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using GalPatchManager.Models;

namespace GalPatchManager.Services;

/// <summary>
/// 自检模式：以 <c>GalPatchManager.exe --selftest</c> 运行，逐项验证核心逻辑并写出报告。
/// 全部在临时目录中进行，不会碰用户的真实游戏目录。
/// </summary>
public static class SelfTest
{
    private static readonly List<string> Lines = new();
    private static int _passed;
    private static int _failed;

    public static int Run(string? reportPath = null)
    {
        Lines.Clear();
        _passed = 0;
        _failed = 0;

        var work = Path.Combine(Path.GetTempPath(), "GalPatchManager-selftest-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(work);

        Section("环境");
        Info($"工作目录: {work}");
        Info($"AppData: {Paths.AppDataRoot}");
        Info($"运行时: {Environment.Version}");

        try
        {
            TestVdf();
            TestMatcher();
            TestGameTypes(work);
            TestHash();
            TestArchive(work);
            TestDownloadMonitor(work);
            TestInstaller(work);
            TestBackupRestore(work);
            TestRollback(work);
            TestDuplicate(work);
            TestExeTrust();
            TestPathGuard();
            TestSearchService();
        }
        catch (Exception ex)
        {
            Fail("自检过程", $"未预期的异常: {ex}");
        }

        Section("结果");
        Info($"通过: {_passed}");
        Info($"失败: {_failed}");

        var summary = _failed == 0
            ? $"全部通过（{_passed} 项）"
            : $"存在失败项：通过 {_passed}，失败 {_failed}";

        var report = new StringBuilder();
        report.AppendLine("游戏补丁安装器 自检报告");
        report.AppendLine($"时间: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        report.AppendLine($"结论: {summary}");
        report.AppendLine(new string('=', 70));
        report.AppendLine(string.Join(Environment.NewLine, Lines));

        var target = reportPath ?? Path.Combine(Paths.AppDataRoot, "selftest-result.txt");

        try
        {
            Paths.EnsureCreated();
            File.WriteAllText(target, report.ToString(), new UTF8Encoding(false));
            System.Diagnostics.Debug.WriteLine(target);
        }
        catch
        {
            // 报告写不出来也不影响退出码
        }

        return _failed == 0 ? 0 : 1;
    }

    // ------------------------------------------------------------------ 用例

    private static void TestGameTypes(string work)
    {
        Section("游戏类型");

        var settings = new AppSettings();
        settings.ApplyDefaults(Paths.AppDataRoot);

        var list = GameTypeCatalog.BuildTypeList(settings);

        Check("内置类型齐全", list.Any(t => t.Name == "视觉小说") && list.Any(t => t.Name == "角色扮演"),
            string.Join(", ", list.Select(t => t.Name)));
        Check("「未分类」始终在最后", list[^1].Name == GameTypeCatalog.Uncategorized, list[^1].Name);

        // ---- 解析优先级：手动 > 自动 > 未分类 ----
        var game = new GameEntry
        {
            Id = "gt1",
            Name = "Type Test",
            AppId = "999001",
            InstallDir = @"C:\NoSuchDir",
            Source = GameSource.Steam,
        };

        Check("无任何信息时为未分类",
            GameTypeCatalog.ResolveType(game, settings) == GameTypeCatalog.Uncategorized,
            GameTypeCatalog.ResolveType(game, settings));

        game.DetectedType = "视觉小说";
        Check("有自动识别结果时采用自动结果",
            GameTypeCatalog.ResolveType(game, settings) == "视觉小说");
        Check("自动结果被标记为自动", GameTypeCatalog.IsAutoDetected(game, settings));

        GameTypeCatalog.SetType(game, settings, "动作");
        Check("手动指定覆盖自动识别",
            GameTypeCatalog.ResolveType(game, settings) == "动作",
            GameTypeCatalog.ResolveType(game, settings));
        Check("手动指定后不再标记为自动", !GameTypeCatalog.IsAutoDetected(game, settings));

        // 覆盖表按 AppID 存，Steam 游戏刷新（条目重建）后依然生效
        var rebuilt = new GameEntry
        {
            Id = "gt1-rebuilt",
            Name = "Type Test",
            AppId = "999001",
            InstallDir = @"C:\NoSuchDir",
            Source = GameSource.Steam,
        };

        Check("Steam 游戏条目重建后类型仍保留",
            GameTypeCatalog.ResolveType(rebuilt, settings) == "动作",
            GameTypeCatalog.ResolveType(rebuilt, settings));

        // 自定义类型应自动进入菜单
        GameTypeCatalog.SetType(rebuilt, settings, "悬疑");
        Check("自定义类型进入类型列表",
            GameTypeCatalog.BuildTypeList(settings).Any(t => t.Name == "悬疑"),
            string.Join(", ", GameTypeCatalog.BuildTypeList(settings).Select(t => t.Name)));

        // ---- 统计 ----
        var games = new List<GameEntry>
        {
            new() { Id = "a", Name = "A", AppId = "1", InstallDir = @"C:\a", DetectedType = "视觉小说" },
            new() { Id = "b", Name = "B", AppId = "2", InstallDir = @"C:\b", DetectedType = "视觉小说" },
            new() { Id = "c", Name = "C", AppId = "3", InstallDir = @"C:\c", DetectedType = "角色扮演" },
            new() { Id = "d", Name = "D", AppId = "4", InstallDir = @"C:\d" },
        };

        var counts = GameTypeCatalog.CountByType(games, settings, GameTypeCatalog.BuildTypeList(settings));

        Check("视觉小说 计数为 2", counts["视觉小说"] == 2, counts["视觉小说"].ToString());
        Check("角色扮演 计数为 1", counts["角色扮演"] == 1, counts["角色扮演"].ToString());
        Check("未分类 计数为 1", counts[GameTypeCatalog.Uncategorized] == 1,
            counts[GameTypeCatalog.Uncategorized].ToString());

        // ---- 启发式识别（用临时目录造特征文件）----
        var renpyDir = Path.Combine(work, "type-renpy");
        Directory.CreateDirectory(renpyDir);
        File.WriteAllText(Path.Combine(renpyDir, "script.rpy"), "label start:");
        File.WriteAllText(Path.Combine(renpyDir, "game.exe"), "MZ");

        var r1 = GameTypeCatalog.DetectFromDirectory(renpyDir);
        Check("识别出 Ren'Py 为视觉小说", r1.Type == "视觉小说", $"{r1.Type} / {r1.Reason}");

        var krkrDir = Path.Combine(work, "type-krkr");
        Directory.CreateDirectory(krkrDir);
        File.WriteAllText(Path.Combine(krkrDir, "data.xp3"), "xp3");
        File.WriteAllText(Path.Combine(krkrDir, "krkr.exe"), "MZ");

        var r2 = GameTypeCatalog.DetectFromDirectory(krkrDir);
        Check("识别出 KiriKiri 为视觉小说", r2.Type == "视觉小说", $"{r2.Type} / {r2.Reason}");

        var rpgDir = Path.Combine(work, "type-rpg");
        Directory.CreateDirectory(rpgDir);
        File.WriteAllText(Path.Combine(rpgDir, "Game.rvdata2"), "x");

        var r3 = GameTypeCatalog.DetectFromDirectory(rpgDir);
        Check("识别出 RPG Maker 为角色扮演", r3.Type == "角色扮演", $"{r3.Type} / {r3.Reason}");

        // 纯文本说明的"游戏"目录不应被误判
        var plainDir = Path.Combine(work, "type-plain");
        Directory.CreateDirectory(plainDir);
        File.WriteAllText(Path.Combine(plainDir, "readme.txt"), "hello");
        File.WriteAllText(Path.Combine(plainDir, "setup.exe"), "MZ");

        var r4 = GameTypeCatalog.DetectFromDirectory(plainDir);
        Check("普通目录不被误判为视觉小说", r4.Type is null, $"{r4.Type} / {r4.Reason}");

        var r5 = GameTypeCatalog.DetectFromDirectory(Path.Combine(work, "not-exist"));
        Check("目录不存在时不误报", r5.Type is null, r5.Reason);

        // ---- genre 映射 ----
        Section("类型映射与自动归类");

        var settings2 = new AppSettings();
        settings2.ApplyDefaults(Paths.AppDataRoot);

        var known = GameTypeCatalog.BuildTypeList(settings2).Select(t => t.Name).ToList();

        var m1 = SteamMetadataService.MapToGameType(
            new[] { "Action", "Adventure", "Indie" }, settings2.GenreTypeMapping, known);
        Check("genres 按映射优先级取首个命中（Adventure 优先于 Action）",
            m1 == "冒险", m1 ?? "(null)");

        var m2 = SteamMetadataService.MapToGameType(
            new[] { "RPG" }, settings2.GenreTypeMapping, known);
        Check("RPG 映射为角色扮演", m2 == "角色扮演", m2 ?? "(null)");

        var m3 = SteamMetadataService.MapToGameType(
            new[] { "Unknown Genre XYZ" }, settings2.GenreTypeMapping, known);
        Check("未知 genre 不映射", m3 is null, m3 ?? "(null)");

        var m4 = SteamMetadataService.MapToGameType(
            Array.Empty<string>(), settings2.GenreTypeMapping, known);
        Check("空 genres 不映射", m4 is null, m4 ?? "(null)");

        // ---- appdetails JSON 解析（离线样本，不联网）----
        const string sample = """
        {"2845270":{"success":true,"data":{"type":"game","name":"100 Aliens Cats",
        "genres":[{"id":"1","description":"Action"},{"id":"25","description":"Adventure"},
        {"id":"4","description":"Casual"},{"id":"23","description":"Indie"}],
        "categories":[{"id":2,"description":"Single-player"}]}}}
        """;

        var parsed = SteamMetadataService.Parse("2845270", sample);

        Check("解析官方元数据成功", parsed is not null);
        Check("解析出游戏名", parsed?.Name == "100 Aliens Cats", parsed?.Name ?? "(null)");
        Check("解析出 4 个 genres", parsed?.Genres.Count == 4,
            parsed is null ? "(null)" : string.Join("/", parsed.Genres));
        Check("解析出 categories", parsed?.Categories.Contains("Single-player") == true);

        var failed = SteamMetadataService.Parse("999", """{"999":{"success":false}}""");
        Check("success=false 时不产生元数据", failed is null);

        var badJson = SteamMetadataService.Parse("1", "not json at all");
        Check("非法 JSON 不崩溃", badJson is null);

        // ---- 归类优先级：手动 > genre > 引擎 > 未分类 ----
        var report = new ClassifyReport();

        var manualGame = new GameEntry
        {
            Id = "m1", Name = "Manual", AppId = "111", InstallDir = Path.Combine(work, "type-renpy"),
        };

        GameTypeCatalog.SetType(manualGame, settings2, "益智");

        var classifier = new AutoClassifier(settings2);

        // allowNetwork=false：即使有 AppID 也不联网，保证自检离线可重复
        var manualResult = classifier.ClassifyAsync(manualGame, report, allowNetwork: false)
            .GetAwaiter().GetResult();

        Check("手动指定的类型不被覆盖", manualResult == "益智", manualResult ?? "(null)");
        Check("手动指定的游戏被计入跳过", report.SkippedManual == 1, report.SkippedManual.ToString());

        // 无手动指定 + 目录含 Ren'Py 特征 -> 引擎识别
        var heurGame = new GameEntry
        {
            Id = "h1", Name = "Heuristic", InstallDir = Path.Combine(work, "type-renpy"),
        };

        var report2 = new ClassifyReport();
        var heurResult = new AutoClassifier(settings2)
            .ClassifyAsync(heurGame, report2, allowNetwork: false)
            .GetAwaiter().GetResult();

        Check("无手动指定时按引擎特征识别", heurResult == "视觉小说", heurResult ?? "(null)");
        Check("计入引擎识别统计", report2.FromHeuristic == 1, report2.FromHeuristic.ToString());

        // 既无元数据也无特征 -> 未分类
        var plain = Path.Combine(work, "type-plain");
        var unknownGame = new GameEntry { Id = "u1", Name = "Unknown", InstallDir = plain };

        var report3 = new ClassifyReport();
        var unknownResult = new AutoClassifier(settings2)
            .ClassifyAsync(unknownGame, report3, allowNetwork: false)
            .GetAwaiter().GetResult();

        Check("无法判断时返回 null（保持未分类）", unknownResult is null, unknownResult ?? "(null)");
        Check("计入未分类统计", report3.Unclassified == 1, report3.Unclassified.ToString());

        // 带 AppID 但禁止联网，且所有本地规则都关掉 -> 不应谎报类型
        var noNetSettings = new AppSettings();
        noNetSettings.ApplyDefaults(Paths.AppDataRoot);
        noNetSettings.EnableNameTypeRule = false;   // 默认名称规则是开启的，这里显式关掉
        noNetSettings.EnableHeuristicType = false;

        TagCache.Clear();                            // 也不能有缓存 genres

        var noNet = new GameEntry
        {
            Id = "n1", Name = "NoNet", AppId = "2845270", InstallDir = plain,
        };

        var report4 = new ClassifyReport();
        var noNetResult = new AutoClassifier(noNetSettings)
            .ClassifyAsync(noNet, report4, allowNetwork: false)
            .GetAwaiter().GetResult();

        Check("离线且无任何本地规则时不谎报类型", noNetResult is null, noNetResult ?? "(null)");

        // ---- 缓存往返 ----
        TagCache.Clear();
        TagCache.Set("app:9999", new GameTagInfo
        {
            Name = "Cached Game",
            Genres = new List<string> { "Adventure", "Indie" },
            Type = "冒险",
            Reason = "测试",
        });

        var cachedBack = TagCache.Get("app:9999");
        Check("标签缓存可写入并读回", cachedBack is not null && cachedBack.Genres.Count == 2,
            cachedBack is null ? "(null)" : string.Join("/", cachedBack.Genres));

        TagCache.Clear();
        Check("标签缓存可清除", TagCache.Get("app:9999") is null);

        // ---- 端到端：缓存里有 genres 时，离线也应能按 genre 归类 ----
        var genreGame = new GameEntry
        {
            Id = "g1",
            Name = "Genre Game",
            AppId = "777001",
            InstallDir = plain, // 目录里没有引擎特征，只能靠 genres
        };

        TagCache.Set(GameTypeCatalog.BuildStableKey(genreGame), new GameTagInfo
        {
            Name = "Genre Game",
            Genres = new List<string> { "RPG", "Indie" },
        });

        var report5 = new ClassifyReport();
        var genreResult = new AutoClassifier(settings2)
            .ClassifyAsync(genreGame, report5, allowNetwork: false)
            .GetAwaiter().GetResult();

        Check("缓存中的 genres 可在离线时完成归类",
            genreResult == "角色扮演", genreResult ?? "(null)");
        Check("归类来源计入 genre 统计", report5.FromGenre == 1, report5.FromGenre.ToString());

        report5.Reasons.TryGetValue(GameTypeCatalog.BuildStableKey(genreGame), out var genreReason);

        Check("归类依据可读且包含原始 genre",
            !string.IsNullOrEmpty(genreReason) && genreReason.Contains("RPG", StringComparison.Ordinal),
            genreReason ?? "(无)");

        // ---- 名称关键词归类（纯离线）----
        var nameSettings = new AppSettings();
        nameSettings.ApplyDefaults(Paths.AppDataRoot);

        var n1 = GameTypeCatalog.DetectFromName("ビジュアルノベル 体験版", @"C:\g\a", nameSettings);
        Check("日文「ビジュアルノベル」识别为视觉小说", n1.Type == "视觉小说", $"{n1.Type} / {n1.Reason}");

        var n2 = GameTypeCatalog.DetectFromName(
            "Some Title", @"C:\games\MyGame ADV Edition", nameSettings);
        Check("目录名里的 ADV 识别为冒险", n2.Type == "冒险", $"{n2.Type} / {n2.Reason}");

        var n3 = GameTypeCatalog.DetectFromName("Plain Name", @"C:\g\plain", nameSettings);
        Check("无关键词时不误报", n3.Type is null, $"{n3.Type} / {n3.Reason}");

        // 名称规则接入整体归类（离线，无 AppID 无引擎特征）
        var nameDir = Path.Combine(work, "type-plain");
        var nameGame = new GameEntry
        {
            Id = "nm1",
            Name = "サンプル ビジュアルノベル",
            InstallDir = nameDir,
        };

        var report6 = new ClassifyReport();
        var nameResult = new AutoClassifier(nameSettings)
            .ClassifyAsync(nameGame, report6, allowNetwork: false)
            .GetAwaiter().GetResult();

        Check("名称关键词可在完全离线时完成归类", nameResult == "视觉小说", nameResult ?? "(null)");
        Check("归类来源计入名称统计", report6.FromName == 1, report6.FromName.ToString());

        // 关闭名称规则后应回到未分类
        nameSettings.EnableNameTypeRule = false;

        var report7 = new ClassifyReport();
        var offResult = new AutoClassifier(nameSettings)
            .ClassifyAsync(nameGame, report7, allowNetwork: false)
            .GetAwaiter().GetResult();

        Check("关闭名称规则后不再按名称归类", offResult is null, offResult ?? "(null)");

        // ---- 空映射表的迁移（老配置里没有这两个字段）----
        var legacy = new AppSettings
        {
            GenreTypeMapping = new Dictionary<string, string>(),
            NameTypeRules = new Dictionary<string, string>(),
        };

        legacy.ApplyDefaults(Paths.AppDataRoot);

        Check("空的 genre 映射表会被补成默认值",
            legacy.GenreTypeMapping.Count > 0 && legacy.GenreTypeMapping.ContainsKey("Adventure"),
            legacy.GenreTypeMapping.Count.ToString());

        Check("空的名称规则表会被补成默认值",
            legacy.NameTypeRules.Count > 0,
            legacy.NameTypeRules.Count.ToString());

        var legacyMapped = SteamMetadataService.MapToGameType(
            new[] { "Adventure", "Indie" },
            legacy.GenreTypeMapping,
            GameTypeCatalog.BuildTypeList(legacy).Select(t => t.Name).ToList());

        Check("迁移后的映射表仍能把 Adventure 映射为冒险", legacyMapped == "冒险", legacyMapped ?? "(null)");

        // ---- 优先级：genres 必须优先于引擎识别 ----
        // 曾经引擎识别排在 genres 之前，导致所有 Unity 游戏被盖成「其他」，
        // 明明有 Adventure/RPG 这种更有用的官方类型却没用上。
        TagCache.Clear();

        var unityDir = Path.Combine(work, "type-unity");
        Directory.CreateDirectory(unityDir);
        File.WriteAllText(Path.Combine(unityDir, "UnityPlayer.dll"), "MZ");

        var unityGame = new GameEntry
        {
            Id = "u2", Name = "Unity Story Game", AppId = "777002", InstallDir = unityDir,
        };

        TagCache.Set(GameTypeCatalog.BuildStableKey(unityGame), new GameTagInfo
        {
            Name = "Unity Story Game",
            Genres = new List<string> { "Adventure", "Indie" },
        });

        var report8 = new ClassifyReport();
        var unityResult = new AutoClassifier(nameSettings)
            .ClassifyAsync(unityGame, report8, allowNetwork: false)
            .GetAwaiter().GetResult();

        Check("有官方 genres 时优先用 genres，不被引擎识别盖成「其他」",
            unityResult == "冒险", unityResult ?? "(null)");
        Check("归类来源计为 genre 而非引擎", report8.FromGenre == 1 && report8.FromHeuristic == 0,
            $"genre={report8.FromGenre} heuristic={report8.FromHeuristic}");

        // 没有 genres 时才回退到引擎识别
        TagCache.Clear();

        var unityOnly = new GameEntry
        {
            Id = "u3", Name = "No Genre Game", AppId = "777003", InstallDir = unityDir,
        };

        var report9 = new ClassifyReport();
        var unityOnlyResult = new AutoClassifier(nameSettings)
            .ClassifyAsync(unityOnly, report9, allowNetwork: false)
            .GetAwaiter().GetResult();

        Check("没有 genres 时才回退到引擎识别",
            unityOnlyResult == "其他" && report9.FromHeuristic == 1,
            $"{unityOnlyResult} / heuristic={report9.FromHeuristic}");

        TagCache.Clear();

        // ---- 库检测：是否原生支持中文 ----
        Section("库标签检测（原生中文 / 补丁状态）");

        Check("识别 Simplified Chinese",
            LibraryStatus.DetectChinese("English, Simplified Chinese, Japanese"));
        Check("识别 Traditional Chinese",
            LibraryStatus.DetectChinese("Traditional Chinese, English"));
        Check("识别裸 Chinese",
            LibraryStatus.DetectChinese("Chinese, English"));
        Check("识别中文写法", LibraryStatus.DetectChinese("英语, 简体中文"));
        Check("纯英文不误判为支持中文",
            !LibraryStatus.DetectChinese("English, French, German"));
        Check("日文不误判为支持中文",
            !LibraryStatus.DetectChinese("Japanese, English"));
        Check("空值不误判", !LibraryStatus.DetectChinese(""));
        Check("null 不误判", !LibraryStatus.DetectChinese(null));

        // 语言字段里的 HTML 标签要被清掉
        var stripped = SteamMetadataService.StripHtml(
            "<strong>English</strong>, Simplified Chinese<br>Japanese");
        Check("语言字段的 HTML 被清除",
            stripped == "English , Simplified Chinese Japanese" || stripped == "English, Simplified Chinese Japanese",
            stripped);

        // ---- 带语言字段的完整解析 ----
        const string langSample = """
        {"2845270":{"success":true,"data":{"name":"Lang Test",
        "genres":[{"id":"25","description":"Adventure"}],
        "supported_languages":"English, Simplified Chinese, Japanese",
        "developers":["Dev A","Dev B"],"publishers":["Pub A"],
        "release_date":{"coming_soon":false,"date":"9 Jun, 2024"}}}}
        """;

        var langMeta = SteamMetadataService.Parse("2845270", langSample);

        Check("解析出 supported_languages",
            langMeta is not null && langMeta.SupportedLanguages.Contains("Simplified Chinese"),
            langMeta?.SupportedLanguages ?? "(null)");
        Check("据此判定支持中文", langMeta?.SupportsChinese == true);
        Check("解析出发行商", langMeta?.Publishers.Contains("Pub A") == true);
        Check("解析出开发商", langMeta?.Developers.Count == 2, langMeta?.Developers.Count.ToString() ?? "(null)");
        Check("解析出发布日期", langMeta?.ReleaseDate == "9 Jun, 2024", langMeta?.ReleaseDate ?? "(null)");

        // 缺字段时不应崩溃
        var minimal = SteamMetadataService.Parse("1", """{"1":{"success":true,"data":{"name":"X"}}}""");
        Check("元数据缺字段时不崩溃", minimal is not null && !minimal.SupportsChinese);

        // ---- 老结构缓存应被识别为需要重抓 ----
        var legacyCache = new GameTagInfo
        {
            Schema = 1,
            Genres = new List<string> { "Adventure" },
        };

        Check("老 schema 缓存被标记为需要重新抓取", legacyCache.IsLegacySchema,
            $"schema={legacyCache.Schema} current={GameTagInfo.CurrentSchema}");

        Check("新写入的缓存不是老 schema",
            !new GameTagInfo().IsLegacySchema);

        // ---- 库检测文本生成 ----
        var zhGame = new GameTagInfo { SupportedLanguages = "English, Simplified Chinese" };
        var jpGame = new GameTagInfo { SupportedLanguages = "Japanese, English" };
        GameTagInfo? noMeta = null;

        Check("原生中文 + 无补丁 -> 原生中文",
            LibraryStatus.Describe(zhGame, 0, 0) == "原生中文",
            LibraryStatus.Describe(zhGame, 0, 0));

        Check("无中文 + 有待处理补丁",
            LibraryStatus.Describe(jpGame, 2, 0) == "无中文 | 待处理补丁 2",
            LibraryStatus.Describe(jpGame, 2, 0));

        Check("无中文 + 已安装补丁",
            LibraryStatus.Describe(jpGame, 0, 1) == "无中文 | 已装 1",
            LibraryStatus.Describe(jpGame, 0, 1));

        Check("未抓元数据时显示未检测（不瞎猜）",
            LibraryStatus.Describe(noMeta, 0, 0) == "未检测",
            LibraryStatus.Describe(noMeta, 0, 0));

        Check("有元数据但无语言字段也算未检测",
            LibraryStatus.Describe(new GameTagInfo(), 0, 0) == "未检测",
            LibraryStatus.Describe(new GameTagInfo(), 0, 0));

        // ---- 社区标签解析（这是「视觉小说」能被识别的关键）----
        Section("Steam 社区标签");

        // 注意：搜索接口返回的是 JSON 转义过的 HTML（属性带反斜杠）。
        // 曾经因为没反转义，正则永远命中不了，导致社区标签全部抓不到。
        const string escaped = """
        {"success":1,"results_html":"\n\t\t\t\t<a href=\"https:\\/\\/store.steampowered.com\\/app\\/1144400\\/SenrenBanka\\/\"\n\t\t\t data-ds-appid=\"1144400\" data-ds-itemkey=\"App_1144400\" data-ds-tagids=\"[3799,9551,3814,3839,4085,4726,597]\" data-ds-descids=\"[1,5]\"\n\t\t   class=\"search_result_row \">\n<\/a>\n","total_count":3}
        """;

        var tagIds = SteamMetadataService.ParseTagIdsForApp(escaped, "1144400");

        Check("能从 JSON 转义的搜索结果里解析出标签 ID",
            tagIds.Count == 7, string.Join(",", tagIds));
        Check("标签 ID 包含 3799（Visual Novel）", tagIds.Contains(3799));
        Check("指定 AppID 不匹配时返回空",
            SteamMetadataService.ParseTagIdsForApp(escaped, "999999").Count == 0);
        Check("空输入不崩溃",
            SteamMetadataService.ParseTagIdsForApp("", "1").Count == 0
            && SteamMetadataService.ParseTagIdsForApp("{}", "").Count == 0);

        // 未转义的 HTML 也应能解析（反转义函数是幂等的）
        var plainHtml = """<a data-ds-appid="42" data-ds-tagids="[3799,4085]">x</a>""";
        Check("未转义的 HTML 同样能解析",
            SteamMetadataService.ParseTagIdsForApp(plainHtml, "42").Count == 2,
            string.Join(",", SteamMetadataService.ParseTagIdsForApp(plainHtml, "42")));

        // ---- 标签 ID -> 名称 ----
        Check("3799 翻成 Visual Novel", SteamTagCatalog.NameOf(3799) == "Visual Novel",
            SteamTagCatalog.NameOf(3799) ?? "(null)");
        Check("4085 翻成 Anime", SteamTagCatalog.NameOf(4085) == "Anime");
        Check("未知 ID 返回 null", SteamTagCatalog.NameOf(99999999) is null);

        var names = SteamTagCatalog.NamesOf(new[] { 3799, 9551, 4085, 99999999 });

        Check("批量翻译只保留已知标签",
            names.Count == 3 && names.Contains("Visual Novel") && names.Contains("Dating Sim"),
            string.Join(" | ", names));

        // ---- 标签 -> 类型映射（视觉小说就靠这一层）----
        var tagSettings = new AppSettings();
        tagSettings.ApplyDefaults(Paths.AppDataRoot);

        var tagKnown = GameTypeCatalog.BuildTypeList(tagSettings).Select(t => t.Name).ToList();

        var vn = SteamTagCatalog.MapToGameType(
            new[] { "Visual Novel", "Adventure", "Anime" }, tagSettings.TagTypeMapping, tagKnown);

        Check("Visual Novel 标签映射为视觉小说", vn == "视觉小说", vn ?? "(null)");

        var ds = SteamTagCatalog.MapToGameType(
            new[] { "Dating Sim", "Casual" }, tagSettings.TagTypeMapping, tagKnown);

        Check("Dating Sim 也映射为视觉小说", ds == "视觉小说", ds ?? "(null)");

        var rpg = SteamTagCatalog.MapToGameType(
            new[] { "JRPG", "RPG" }, tagSettings.TagTypeMapping, tagKnown);

        Check("JRPG 映射为角色扮演", rpg == "角色扮演", rpg ?? "(null)");

        var noneTag = SteamTagCatalog.MapToGameType(
            new[] { "Unknown Tag XYZ" }, tagSettings.TagTypeMapping, tagKnown);

        Check("未知标签不映射", noneTag is null, noneTag ?? "(null)");

        // 空映射表要能补上默认值，否则视觉小说永远分不出来
        var legacyTag = new AppSettings
        {
            TagTypeMapping = new Dictionary<string, string>(),
        };

        legacyTag.ApplyDefaults(Paths.AppDataRoot);

        Check("空的社区标签映射表会补成默认值",
            legacyTag.TagTypeMapping.Count > 0 && legacyTag.TagTypeMapping.ContainsKey("Visual Novel"),
            legacyTag.TagTypeMapping.Count.ToString());

        // ---- 缓存能保存社区标签 ----
        TagCache.Clear();

        var tagCacheGame = new GameTagInfo
        {
            Name = "Tag Cache Test",
            CommunityTagIds = new List<int> { 3799, 4085 },
            CommunityTags = new List<string> { "Visual Novel", "Anime" },
        };

        TagCache.Set("app:tagtest", tagCacheGame);

        var tagBack = TagCache.Get("app:tagtest");

        Check("社区标签能写入缓存并读回",
            tagBack is not null && tagBack.CommunityTags.Count == 2
            && tagBack.CommunityTagIds.Contains(3799),
            tagBack is null ? "(null)" : string.Join(" | ", tagBack.CommunityTags));

        TagCache.Clear();
    }

    private static void TestVdf()
    {
        Section("VDF / ACF 解析");

        const string nested = """
            "libraryfolders"
            {
                "0"
                {
                    "path"		"C:\\Program Files (x86)\\Steam"
                    "label"		""
                    "apps"
                    {
                        "730"		"1234567890"
                    }
                }
                "1"
                {
                    "path"		"E:\\SteamLibrary"
                }
            }
            """;

        var root = HandleVdf.Parse(nested);
        var container = root.FindChild("libraryfolders");

        Check("找到 libraryfolders 根节点", container is not null);

        var node0 = container?.FindChild("0");
        var node1 = container?.FindChild("1");

        Check("解析出库 0 的 path", node0?.GetValue("path") == @"C:\Program Files (x86)\Steam",
            node0?.GetValue("path"));

        Check("解析出库 1 的 path", node1?.GetValue("path") == @"E:\SteamLibrary",
            node1?.GetValue("path"));

        Check("嵌套 apps 子节点存在", node0?.FindChild("apps") is not null);

        // 老的扁平写法
        const string legacy = """
            "LibraryFolders"
            {
                "TimeNextStatsReport"	"1234"
                "1"		"D:\\SteamLibrary"
                "2"		"E:\\Games"
            }
            """;

        var legacyRoot = HandleVdf.Parse(legacy);
        var legacyContainer = legacyRoot.FindChild("LibraryFolders");

        Check("兼容老版扁平写法（数字键=路径）",
            legacyContainer?.GetValue("1") == @"D:\SteamLibrary",
            legacyContainer?.GetValue("1"));

        // ACF 清单
        const string acf = """
            "AppState"
            {
                "appid"		"1234567"
                "name"		"Test Galgame"
                "installdir"		"Test Galgame"
                "StateFlags"		"4"
            }
            """;

        var acfRoot = HandleVdf.Parse(acf);
        var state = acfRoot.FindChild("AppState");

        Check("解析 ACF name", state?.GetValue("name") == "Test Galgame");
        Check("解析 ACF appid", state?.GetValue("appid") == "1234567");
        Check("解析 ACF installdir", state?.GetValue("installdir") == "Test Galgame");

        // 容错：不完整/空内容不应该抛异常
        try
        {
            HandleVdf.Parse("\"a\" { \"b\" \"c\"");
            HandleVdf.Parse("");
            HandleVdf.Parse("} } {");
            Pass("畸形 VDF 不抛异常");
        }
        catch (Exception ex)
        {
            Fail("畸形 VDF 容错", ex.Message);
        }
    }

    private static void TestMatcher()
    {
        Section("补丁与游戏匹配");

        var games = new List<GameEntry>
        {
            new() { Id = "g1", Name = "千恋万花", AppId = "1234567", InstallDir = @"C:\Games\Senren" },
            new() { Id = "g2", Name = "ATRI -My Dear Moments-", AppId = "7654321", InstallDir = @"C:\Games\ATRI" },
        };

        foreach (var g in games) g.NormalizedName = NameMatcher.Normalize(g.Name);

        Check("规范化剔除噪声词", !NameMatcher.Normalize("ATRI [汉化补丁]").Contains("汉化"));

        var appIdMatch = NameMatcher.FindMatch("appmanifest_1234567.7z", games);
        Check("文件名中的 AppID 命中游戏", appIdMatch.Game?.Id == "g1", appIdMatch.Reason);

        var appIdMatch2 = NameMatcher.FindMatch("ATRI_patch[7654321].zip", games);
        Check("方括号中的 AppID 命中游戏", appIdMatch2.Game?.Id == "g2", appIdMatch2.Reason);

        var keywordGames = new List<GameEntry>
        {
            new() { Id = "g3", Name = "Some Game", InstallDir = @"C:\Games\Some", MatchKeywords = { "somegame" } },
        };

        var kw = NameMatcher.FindMatch("somegame_v1.2_patch.zip", keywordGames);
        Check("用户关键词命中游戏", kw.Game?.Id == "g3", kw.Reason);

        var rules = new List<MatchRuleEntry>
        {
            new() { Keyword = "senren", GameId = "g1", Enabled = true },
        };

        var byRule = NameMatcher.FindMatch("senren_patch_fix.rar", games, rules);
        Check("匹配规则命中游戏", byRule.Game?.Id == "g1", byRule.Reason);

        var fuzzy = NameMatcher.FindMatch("ATRI -My Dear Moments- 中文补丁.zip", games);
        Check("名称模糊匹配命中", fuzzy.Game?.Id == "g2", fuzzy.Reason);

        var none = NameMatcher.FindMatch("completely_unrelated_thing.zip",
            new List<GameEntry> { new() { Id = "x", Name = "Zzzzz", InstallDir = @"C:\z" } });
        Check("无关文件不会误判为高置信度", !none.IsConfident, $"score={none.Score:F2}");

        Check("AppID 提取", NameMatcher.ExtractAppId("app_987654_patch.zip") == "987654",
            NameMatcher.ExtractAppId("app_987654_patch.zip"));

        Check("文件名剥离扩展名",
            NameMatcher.FileNameToProbe(@"C:\dl\Senren.patch.v2.zip") == "Senren.patch.v2",
            NameMatcher.FileNameToProbe(@"C:\dl\Senren.patch.v2.zip"));
    }

    private static void TestHash()
    {
        Section("哈希与文件完整性");

        var file = Path.Combine(Path.GetTempPath(), "galpatch-hash-" + Guid.NewGuid().ToString("N")[..8] + ".txt");
        File.WriteAllBytes(file, Encoding.UTF8.GetBytes("hello galgame"));

        var hash = FileHash.Sha256(file);
        var expected = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes("hello galgame")))
            .ToLowerInvariant();

        Check("SHA-256 计算正确", hash == expected, hash);

        Check("可完整读取正常文件", FileHash.CanBeReadFully(file, out var err), err ?? "");

        // 被独占锁定的文件应当判定为不可读
        var locked = Path.Combine(Path.GetTempPath(), "galpatch-lock-" + Guid.NewGuid().ToString("N")[..8] + ".bin");
        File.WriteAllText(locked, "data");

        using (var stream = new FileStream(locked, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var ok = FileHash.CanBeReadFully(locked, out var reason);
            Check("被占用的文件判定为不可读", !ok, reason ?? "(无原因)");
            stream.Close();
        }

        Check("不存在的文件判定为不可读", !FileHash.CanBeReadFully(file + ".missing", out _));

        TryDelete(file);
        TryDelete(locked);
    }

    private static void TestArchive(string work)
    {
        Section("压缩包解析与解压（含路径穿越防护）");

        var zipPath = Path.Combine(work, "Senren_patch.zip");

        using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            AddEntry(zip, "Senren/patch.xp3", "patch-content");
            AddEntry(zip, "Senren/data/readme.txt", "readme-content");
            AddEntry(zip, "Senren/汉化说明.txt", "中文说明");
        }

        var info = ArchiveService.Probe(zipPath);

        Check("识别为 ZIP", info.Kind == ArchiveKind.Zip, info.KindText);
        Check("条目数正确（3 个文件）", info.FileCount == 3, info.FileCount.ToString());
        Check("识别出统一根目录 Senren", info.RootPrefix == "Senren", info.RootPrefix ?? "(null)");

        var dest = Path.Combine(work, "extract1");
        var result = ArchiveService.Extract(zipPath, dest, preferOriginalStructure: false);

        Check("解压成功", result.Success, result.Error);
        Check("解压文件数正确", result.FilesExtracted == 3, result.FilesExtracted.ToString());
        Check("剥离根目录后 patch.xp3 位于目标根", File.Exists(Path.Combine(dest, "patch.xp3")));
        Check("保留子目录结构", File.Exists(Path.Combine(dest, "data", "readme.txt")));
        Check("中文文件名正确处理", File.Exists(Path.Combine(dest, "汉化说明.txt")));

        // 保留原始结构
        var dest2 = Path.Combine(work, "extract2");
        var result2 = ArchiveService.Extract(zipPath, dest2, preferOriginalStructure: true);

        Check("保留原结构时仍含根目录",
            result2.Success && File.Exists(Path.Combine(dest2, "Senren", "patch.xp3")));

        // ---- 恶意压缩包：路径穿越 ----
        var evilPath = Path.Combine(work, "evil.zip");

        using (var zip = ZipFile.Open(evilPath, ZipArchiveMode.Create))
        {
            AddEntry(zip, "../escaped.txt", "should-not-escape");
            AddEntry(zip, "..\\..\\escaped2.txt", "should-not-escape");
            AddEntry(zip, "ok.txt", "fine");
        }

        var evilInfo = ArchiveService.Probe(evilPath);
        Info($"恶意压缩包条目: {string.Join(", ", evilInfo.Entries.Select(e => e.FullPath))}");

        var evilDest = Path.Combine(work, "evil-extract");
        var evilResult = ArchiveService.Extract(evilPath, evilDest, preferOriginalStructure: false);

        var escapedFile = Path.Combine(work, "escaped.txt");
        var escapedFile2 = Path.Combine(work, "escaped2.txt");

        Check("穿越条目没有逃出目标目录",
            !File.Exists(escapedFile) && !File.Exists(escapedFile2) &&
            !File.Exists(Path.Combine(Path.GetDirectoryName(work)!, "escaped.txt")),
            $"escaped={File.Exists(escapedFile)}, escaped2={File.Exists(escapedFile2)}");

        Info($"恶意压缩包处理结果: Success={evilResult.Success}, Error={evilResult.Error}");

        // 直接验证守卫函数
        Check("守卫拒绝 .. 路径",
            !ArchiveService.TryGetSafeTarget(evilDest, "../x.txt", out _, out _));

        Check("守卫拒绝盘符路径",
            !ArchiveService.TryGetSafeTarget(evilDest, "C:/Windows/x.txt", out _, out _));

        Check("守卫拒绝 UNC 路径",
            !ArchiveService.TryGetSafeTarget(evilDest, "\\\\server\\share\\x.txt", out _, out _));

        Check("守卫接受正常相对路径",
            ArchiveService.TryGetSafeTarget(evilDest, "sub/dir/file.dat", out var safe, out _)
            && safe.StartsWith(evilDest, StringComparison.OrdinalIgnoreCase),
            safe);

        // 空的/损坏的压缩包不应崩溃
        var broken = Path.Combine(work, "broken.zip");
        File.WriteAllBytes(broken, new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 });

        var brokenOk = ArchiveService.CanOpen(broken, out var brokenErr);
        Check("损坏压缩包打开失败但不崩溃", !brokenOk, brokenErr ?? "(无错误信息)");
    }

    private static void TestDownloadMonitor(string work)
    {
        Section("下载监控与完成判定");

        var watchDir = Path.Combine(work, "downloads");
        Directory.CreateDirectory(watchDir);

        // 基线会持久化到 AppData，必须清掉，否则上一次自检留下的基线会让本次全部跳过
        IgnoreBaseline.Clear(watchDir);

        var settings = new AppSettings
        {
            DownloadFolder = watchDir,
            PollIntervalSeconds = 1,
            StableChecksRequired = 3,
            // 自检里不等真实秒数
            MinQuietSeconds = 0,
            // 体积过滤会在下面单独验证，这里先关掉
            MinFileSizeMB = 0,
            // 这一组测的是“完成判定”，不是基线过滤，所以关闭新增文件过滤，
            // 否则本测试自己造的文件会被基线挡掉。基线过滤在下一节单独验证。
            OnlyNewFiles = false,
        };

        settings.ApplyDefaults(Paths.AppDataRoot);

        using var monitor = new DownloadMonitor(settings);

        Check("忽略 .crdownload 临时文件", !monitor.IsCandidate(Path.Combine(watchDir, "patch.zip.crdownload")));
        Check("忽略 .part 文件", !monitor.IsCandidate(Path.Combine(watchDir, "patch.7z.part")));
        Check("忽略 .tmp 文件", !monitor.IsCandidate(Path.Combine(watchDir, "patch.rar.tmp")));
        Check("忽略隐藏临时文件", !monitor.IsCandidate(Path.Combine(watchDir, ".patch.zip")));
        Check("忽略无扩展名文件", !monitor.IsCandidate(Path.Combine(watchDir, "patch")));
        Check("接受 .zip 补丁", monitor.IsCandidate(Path.Combine(watchDir, "patch.zip")));
        Check("接受 .7z 补丁", monitor.IsCandidate(Path.Combine(watchDir, "patch.7z")));
        Check("接受 .rar 补丁", monitor.IsCandidate(Path.Combine(watchDir, "patch.rar")));
        Check("接受 .exe 补丁", monitor.IsCandidate(Path.Combine(watchDir, "patch.exe")));
        Check("忽略无关类型 .mp4", !monitor.IsCandidate(Path.Combine(watchDir, "video.mp4")));

        // 构造一个真实的 zip 并放到监控目录
        var patchZip = Path.Combine(watchDir, "ATRI_patch.zip");

        using (var zip = ZipFile.Open(patchZip, ZipArchiveMode.Create))
        {
            AddEntry(zip, "patch.xp3", "content-v1");
        }

        PatchFileFoundEventArgs? found = null;
        var progressSeen = 0;

        monitor.PatchFileFound += (_, e) => found = e;
        monitor.Progress += (_, _) => progressSeen++;

        // 第一步：首次扫描只登记，不应判定完成
        monitor.ScanOnce();
        Check("首次扫描不立即判定完成", found is null, found?.FilePath ?? "(未触发)");

        // 后续扫描：大小稳定后才应判定完成
        var matchedAt = -1;

        for (var i = 1; i <= 6 && found is null; i++)
        {
            Thread.Sleep(120);
            monitor.ScanOnce();

            if (found is not null) matchedAt = i;
        }

        Check("大小稳定后判定下载完成", found is not null,
            found is null ? "6 次扫描后仍未判定完成" : $"第 {matchedAt} 次扫描判定完成");

        Check("完成事件包含正确的文件路径",
            found is not null && string.Equals(found.FilePath, patchZip, StringComparison.OrdinalIgnoreCase));

        Check("完成事件包含 SHA-256", found is not null && found.Sha256.Length == 64,
            found?.Sha256 ?? "(无)");

        Check("完成事件包含文件大小", found is not null && found.Size > 0, found?.Size.ToString() ?? "(无)");
        Check("产生了进度通知", progressSeen > 0, progressSeen.ToString());

        // ---- 同一文件内容持续变化（大小不变）时绝不能判定完成 ----
        // 这是“覆盖下载 / 断点续传重写”的典型形态：长度可能不变，但内容仍在变。
        var mutating = Path.Combine(watchDir, "mutating.zip");

        using (var zip = ZipFile.Open(mutating, ZipArchiveMode.Create))
        {
            AddEntry(zip, "payload.bin", new string('A', 20000));
        }

        var mutatingFound = false;
        using var monitor2 = new DownloadMonitor(settings);

        // 只关心这个文件的事件，避免目录里其他稳定文件干扰判定
        monitor2.PatchFileFound += (_, e) =>
        {
            if (string.Equals(e.FilePath, mutating, StringComparison.OrdinalIgnoreCase)) mutatingFound = true;
        };

        monitor2.ScanOnce();

        for (var i = 0; i < 8 && !mutatingFound; i++)
        {
            Thread.Sleep(90);

            // 等长改写中间内容，不改变文件长度
            using (var stream = new FileStream(mutating, FileMode.Open, FileAccess.Write, FileShare.ReadWrite))
            {
                stream.Seek(60, SeekOrigin.Begin);
                stream.Write(Encoding.UTF8.GetBytes($"CHG{i:D5}"));
            }

            monitor2.ScanOnce();
        }

        Check("等长内容持续变化时不判定完成", !mutatingFound,
            mutatingFound ? "被错误判定为完成" : "");

        // ---- 真实增长中的文件也不能判定完成 ----
        var growing = Path.Combine(watchDir, "growing-download.zip");

        File.WriteAllBytes(growing, new byte[4096]);

        var growingFound = false;
        using var monitor2b = new DownloadMonitor(settings);

        monitor2b.PatchFileFound += (_, e) =>
        {
            if (string.Equals(e.FilePath, growing, StringComparison.OrdinalIgnoreCase)) growingFound = true;
        };

        monitor2b.ScanOnce();

        for (var i = 0; i < 6; i++)
        {
            Thread.Sleep(80);

            using (var stream = new FileStream(growing, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
            {
                stream.Write(new byte[64 * 1024]);
            }

            monitor2b.ScanOnce();
        }

        Check("持续增长的文件不判定完成", !growingFound,
            growingFound ? "被错误判定为完成" : "");

        // ---- 静默时长门槛 ----
        var quietSettings = new AppSettings
        {
            DownloadFolder = watchDir,
            PollIntervalSeconds = 1,
            StableChecksRequired = 2,
            MinQuietSeconds = 30,
        };

        quietSettings.ApplyDefaults(Paths.AppDataRoot);

        // 目录里已有的稳定文件不应因“次数够了”就被判定完成，因为还没到最短静默时长
        var quietFound = false;
        var quietFile = "";

        using var monitor3 = new DownloadMonitor(quietSettings);
        monitor3.PatchFileFound += (_, e) =>
        {
            quietFound = true;
            quietFile = Path.GetFileName(e.FilePath);
        };

        monitor3.ScanOnce();
        Thread.Sleep(80);
        monitor3.ScanOnce();
        Thread.Sleep(80);
        monitor3.ScanOnce();

        Check("未达到最短静默时长时不判定完成", !quietFound,
            quietFound ? $"被错误判定为完成: {quietFile}" : "");

        // ---------------- 基线过滤：只处理新下载的文件 ----------------
        Section("只处理新增文件（基线过滤）");

        var baseDir = Path.Combine(work, "baseline-dir");
        Directory.CreateDirectory(baseDir);

        // 模拟“下载目录里早就有一堆安装包”
        var old1 = Path.Combine(baseDir, "SteamSetup.exe");
        var old2 = Path.Combine(baseDir, "ChromeSetup.exe");
        var old3 = Path.Combine(baseDir, "SomeTool.zip");
        var old4 = Path.Combine(baseDir, "AliceInCradle.7z");

        foreach (var f in new[] { old1, old2, old3, old4 })
        {
            File.WriteAllBytes(f, new byte[8 * 1024]);
        }

        var baseSettings = new AppSettings
        {
            DownloadFolder = baseDir,
            PollIntervalSeconds = 1,
            StableChecksRequired = 1,
            MinQuietSeconds = 0,
            MinFileSizeMB = 0,
            OnlyNewFiles = true,
        };

        baseSettings.ApplyDefaults(Paths.AppDataRoot);

        // 清掉可能残留的基线，保证从干净状态开始
        IgnoreBaseline.Clear(baseDir);

        var baselineFound = new List<string>();

        using (var m = new DownloadMonitor(baseSettings))
        {
            m.PatchFileFound += (_, e) => baselineFound.Add(Path.GetFileName(e.FilePath));

            // Start 里会自动建立基线；这里直接调 EnsureBaseline 走同一条路径
            m.EnsureBaseline();

            Check("建立基线后报告已忽略文件数",
                IgnoreBaseline.HasBaseline(baseDir) && IgnoreBaseline.CountOf(baseDir) == 4,
                $"count={IgnoreBaseline.CountOf(baseDir)}");

            m.ScanOnce();
            m.ScanOnce();

            Check("监控目录里已存在的文件不会被当作待处理补丁",
                baselineFound.Count == 0,
                baselineFound.Count == 0 ? "" : string.Join(", ", baselineFound));
        }

        // 新下载一个补丁：应当被识别
        var newPatch = Path.Combine(baseDir, "AliceInCradle 汉化补丁.zip");

        using (var zip = ZipFile.Open(newPatch, ZipArchiveMode.Create))
        {
            AddEntry(zip, "patch.xp3", "content");
        }

        // 确保修改时间晚于基线建立时间
        File.SetLastWriteTimeUtc(newPatch, DateTime.UtcNow.AddSeconds(2));

        var found2 = new List<string>();

        using (var m2 = new DownloadMonitor(baseSettings))
        {
            m2.PatchFileFound += (_, e) => found2.Add(Path.GetFileName(e.FilePath));

            // 先确认过滤逻辑本身判定为“新增”
            var probeInfo = new FileInfo(newPatch);

            Check("新增文件通过基线过滤",
                IgnoreBaseline.IsNewOrChanged(
                    baseDir, newPatch, probeInfo.Length, probeInfo.LastWriteTimeUtc),
                "IsNewOrChanged 返回 false");

            // 直接驱动评估，不依赖扫描循环的时序。
            // 注意：第一次调用只登记大小并重新计数，所以至少要跑两轮。
            var evalMethod = typeof(DownloadMonitor).GetMethod(
                "Evaluate", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

            if (evalMethod is null)
            {
                Info("诊断: 未找到 Evaluate 方法");
            }
            else
            {
                try
                {
                    for (var attempt = 0; attempt < 3 && found2.Count == 0; attempt++)
                    {
                        evalMethod.Invoke(m2, new object[] { newPatch, CancellationToken.None });
                    }
                }
                catch (System.Reflection.TargetInvocationException tie)
                {
                    Info($"诊断: Evaluate 抛出异常 -> {tie.InnerException?.GetType().Name}: {tie.InnerException?.Message}");
                }
            }

            Check("之后新下载的补丁能被正常识别",
                found2.Count == 1 && found2[0] == "AliceInCradle 汉化补丁.zip",
                found2.Count == 0 ? "未识别到新补丁" : string.Join(", ", found2));
        }

        // 已处理过的文件不应重复触发
        var found3 = new List<string>();

        using (var m3 = new DownloadMonitor(baseSettings))
        {
            m3.PatchFileFound += (_, e) => found3.Add(Path.GetFileName(e.FilePath));
            m3.ScanOnce();
            m3.ScanOnce();

            Check("已完成处理的文件不会重复触发", found3.Count == 0,
                found3.Count == 0 ? "" : string.Join(", ", found3));
        }

        // 关闭“只处理新增文件”后，目录里所有匹配文件都会被列出
        var offDir = Path.Combine(work, "off-dir");
        Directory.CreateDirectory(offDir);

        IgnoreBaseline.Clear(offDir);

        // 必须都是**可正常打开**的压缩包，否则会停在第 3 条判定（压缩包可打开）上
        foreach (var name in new[] { "a.zip", "b.zip", "c.zip", "d.zip" })
        {
            using var zip = ZipFile.Open(Path.Combine(offDir, name), ZipArchiveMode.Create);
            AddEntry(zip, "payload.bin", new string('x', 4096));
        }

        var allSettings = new AppSettings
        {
            DownloadFolder = offDir,
            PollIntervalSeconds = 1,
            StableChecksRequired = 1,
            MinQuietSeconds = 0,
            MinFileSizeMB = 0,
            OnlyNewFiles = false,
        };

        allSettings.ApplyDefaults(Paths.AppDataRoot);

        var foundAll = new List<string>();

        using (var m4 = new DownloadMonitor(allSettings))
        {
            m4.PatchFileFound += (_, e) => foundAll.Add(Path.GetFileName(e.FilePath));

            for (var i = 0; i < 4; i++)
            {
                m4.ScanOnce();
                Thread.Sleep(120);
            }

            Check("关闭该开关时会处理目录内全部匹配文件",
                foundAll.Count == 4,
                $"识别到 {foundAll.Count} 个: {string.Join(", ", foundAll)}");
        }

        // ---------------- 回归：完成判定需要多轮扫描，不能被基线提前拦住 ----------------
        // 曾经的问题：文件第一次被发现时就立刻登记进基线，导致后续扫描把它当成
        // “已存在文件”跳过，稳定计数永远累积不到要求值，补丁永远无法判定完成。
        var multiScanDir = Path.Combine(work, "multiscan-dir");
        Directory.CreateDirectory(multiScanDir);

        IgnoreBaseline.Clear(multiScanDir);

        var multiSettings = new AppSettings
        {
            DownloadFolder = multiScanDir,
            PollIntervalSeconds = 1,
            StableChecksRequired = 3,
            MinQuietSeconds = 0,
            MinFileSizeMB = 0,
            OnlyNewFiles = true,
        };

        multiSettings.ApplyDefaults(Paths.AppDataRoot);

        var multiFound = new List<string>();

        using (var m6 = new DownloadMonitor(multiSettings))
        {
            m6.EnsureBaseline();

            var patch = Path.Combine(multiScanDir, "AliceInCradle 汉化补丁.zip");

            using (var zip = ZipFile.Open(patch, ZipArchiveMode.Create))
            {
                AddEntry(zip, "patch.xp3", "content");
            }

            File.SetLastWriteTimeUtc(patch, DateTime.UtcNow.AddSeconds(2));

            m6.PatchFileFound += (_, e) => multiFound.Add(Path.GetFileName(e.FilePath));

            // 用多轮扫描驱动：每轮之间文件不变，稳定计数应当逐步累积
            for (var i = 0; i < 8 && multiFound.Count == 0; i++)
            {
                m6.ScanOnce();
                if (multiFound.Count == 0) Thread.Sleep(60);
            }

            Check("多轮扫描后仍能判定完成（基线不得提前拦住）",
                multiFound.Count == 1,
                multiFound.Count == 0 ? "多轮扫描后仍未判定完成" : string.Join(", ", multiFound));

            // 判定完成后才登记进基线，后续扫描不应重复触发
            var repeat = 0;
            m6.PatchFileFound += (_, _) => repeat++;

            m6.ScanOnce();
            m6.ScanOnce();

            Check("判定完成后文件被登记进基线，不再重复触发", repeat == 0,
                repeat == 0 ? "" : $"重复触发 {repeat} 次");
        }

        // ---------------- 最小体积过滤 ----------------
        var sizeDir = Path.Combine(work, "size-dir");
        Directory.CreateDirectory(sizeDir);

        IgnoreBaseline.Clear(sizeDir);

        File.WriteAllBytes(Path.Combine(sizeDir, "tiny.zip"), new byte[1024]);          // 1 KB
        File.WriteAllBytes(Path.Combine(sizeDir, "big.zip"), new byte[3 * 1024 * 1024]); // 3 MB

        var sizeSettings = new AppSettings
        {
            DownloadFolder = sizeDir,
            PollIntervalSeconds = 1,
            StableChecksRequired = 1,
            MinQuietSeconds = 0,
            OnlyNewFiles = false,
            MinFileSizeMB = 2,
        };

        sizeSettings.ApplyDefaults(Paths.AppDataRoot);

        var foundSmall = new List<string>();

        using (var m5 = new DownloadMonitor(sizeSettings))
        {
            m5.PatchFileFound += (_, e) => foundSmall.Add(Path.GetFileName(e.FilePath));

            for (var i = 0; i < 4; i++)
            {
                m5.ScanOnce();
                Thread.Sleep(100);
            }
        }

        // tiny.zip 只有 1KB，会被体积过滤挡掉（big.zip 是无效压缩包，会因打不开而等待）
        Check("小于体积下限的文件被忽略",
            !foundSmall.Contains("tiny.zip"),
            string.Join(", ", foundSmall));
    }

    private static void TestInstaller(string work)
    {
        Section("补丁安装");

        var gameDir = Path.Combine(work, "game");
        Directory.CreateDirectory(gameDir);

        // 已存在的文件（将被覆盖，测试备份）
        File.WriteAllText(Path.Combine(gameDir, "existing.xp3"), "original-content");

        var extractRoot = Path.Combine(work, "extract-install");
        Directory.CreateDirectory(Path.Combine(extractRoot, "data"));

        File.WriteAllText(Path.Combine(extractRoot, "patch.xp3"), "patched-content");
        File.WriteAllText(Path.Combine(extractRoot, "existing.xp3"), "new-content");
        File.WriteAllText(Path.Combine(extractRoot, "data", "asset.dat"), "asset-data");

        var settings = new AppSettings
        {
            BackupBeforeOverwrite = true,
            AutoRollbackOnFailure = true,
        };

        settings.ApplyDefaults(Paths.AppDataRoot);

        var installer = new InstallService(settings);

        var game = new GameEntry
        {
            Id = "test-game",
            Name = "Test Game",
            InstallDir = gameDir,
            Source = GameSource.Manual,
        };

        var plan = installer.PlanFromExtractDirectory(extractRoot, gameDir);

        Check("计划包含 3 个文件", plan.Count == 3, plan.Count.ToString());
        Check("计划识别出 1 个覆盖", plan.Count(s => s.TargetExists) == 1,
            plan.Count(s => s.TargetExists).ToString());
        Check("计划中没有被拒绝的条目", plan.All(s => !s.IsBlocked),
            string.Join(";", plan.Where(s => s.IsBlocked).Select(s => s.BlockReason)));

        var report = installer.InstallAsync(plan, game, "testkey001").GetAwaiter().GetResult();

        Check("安装成功", report.Success, report.Error);
        Check("复制文件数为 3", report.FilesCopied == 3, report.FilesCopied.ToString());
        Check("备份了 1 个被覆盖文件", report.FilesBackedUp == 1, report.FilesBackedUp.ToString());
        Check("新增文件已就位", File.Exists(Path.Combine(gameDir, "patch.xp3")));
        Check("子目录文件已就位", File.Exists(Path.Combine(gameDir, "data", "asset.dat")));
        Check("被覆盖文件内容已更新",
            File.ReadAllText(Path.Combine(gameDir, "existing.xp3")) == "new-content",
            File.ReadAllText(Path.Combine(gameDir, "existing.xp3")));
        Check("备份文件真实存在",
            report.Backups.Count == 1 && File.Exists(report.Backups[0].BackupPath),
            report.Backups.FirstOrDefault()?.BackupPath ?? "(无)");
        Check("备份内容为原文件内容",
            report.Backups.Count == 1 &&
            File.ReadAllText(report.Backups[0].BackupPath) == "original-content",
            report.Backups.Count == 1 ? File.ReadAllText(report.Backups[0].BackupPath) : "(无)");
        Check("没有残留 .galpatch-new 临时文件",
            !Directory.EnumerateFiles(gameDir, "*.galpatch-new", SearchOption.AllDirectories).Any());
    }

    private static void TestBackupRestore(string work)
    {
        Section("备份还原");

        var gameDir = Path.Combine(work, "game2");
        Directory.CreateDirectory(gameDir);

        File.WriteAllText(Path.Combine(gameDir, "data.xp3"), "v1-original");

        var settings = new AppSettings { BackupBeforeOverwrite = true };
        settings.ApplyDefaults(Paths.AppDataRoot);

        var installer = new InstallService(settings);

        var backup = installer.BackupFileAsync(Path.Combine(gameDir, "data.xp3"), "g", "k", "batch-test")
            .GetAwaiter().GetResult();

        Check("备份成功", backup is not null, backup?.BackupPath ?? "(null)");

        File.WriteAllText(Path.Combine(gameDir, "data.xp3"), "v2-overwritten");

        Check("文件已被改写",
            File.ReadAllText(Path.Combine(gameDir, "data.xp3")) == "v2-overwritten");

        var restored = installer.RestoreBackups(new[] { backup! });

        Check("还原调用成功", restored == 1, restored.ToString());
        Check("文件内容已还原为 v1",
            File.ReadAllText(Path.Combine(gameDir, "data.xp3")) == "v1-original",
            File.ReadAllText(Path.Combine(gameDir, "data.xp3")));
    }

    private static void TestRollback(string work)
    {
        Section("失败回滚");

        var gameDir = Path.Combine(work, "game3");
        Directory.CreateDirectory(gameDir);

        File.WriteAllText(Path.Combine(gameDir, "keep.xp3"), "keep-original");

        var extractRoot = Path.Combine(work, "extract-rollback");
        Directory.CreateDirectory(extractRoot);

        File.WriteAllText(Path.Combine(extractRoot, "keep.xp3"), "keep-new");
        File.WriteAllText(Path.Combine(extractRoot, "brand-new.xp3"), "brand-new");

        var settings = new AppSettings
        {
            BackupBeforeOverwrite = true,
            AutoRollbackOnFailure = true,
        };

        settings.ApplyDefaults(Paths.AppDataRoot);

        var installer = new InstallService(settings);

        var game = new GameEntry { Id = "g3", Name = "Rollback Game", InstallDir = gameDir };

        var plan = installer.PlanFromExtractDirectory(extractRoot, gameDir).ToList();

        // 人为破坏：把第二个真实步骤的源文件删掉，制造中途失败
        var victim = plan.First(s => s.SourcePath.EndsWith("brand-new.xp3", StringComparison.OrdinalIgnoreCase));
        File.Delete(victim.SourcePath);

        var report = installer.InstallAsync(plan, game, "rollbackkey").GetAwaiter().GetResult();

        Check("安装被判定为失败", !report.Success, report.Error);
        Check("报告标明已回滚", report.RolledBack);
        Check("被覆盖文件已还原",
            File.ReadAllText(Path.Combine(gameDir, "keep.xp3")) == "keep-original",
            File.ReadAllText(Path.Combine(gameDir, "keep.xp3")));
        Check("失败的安装没有被报告为成功", report.FilesCopied == 0 || report.RolledBack,
            $"filesCopied={report.FilesCopied}");
    }

    private static void TestDuplicate(string work)
    {
        Section("重复安装检测");

        var patchFile = Path.Combine(work, "dup-patch.zip");
        File.WriteAllText(patchFile, "some-content-for-key");

        var key1 = InstallService.BuildPatchKey(patchFile);
        var key2 = InstallService.BuildPatchKey(patchFile);

        Check("相同文件的补丁键一致", key1 == key2, $"{key1} / {key2}");

        var other = Path.Combine(work, "dup-patch-2.zip");
        File.WriteAllText(other, "different-length-content-for-key");

        var key3 = InstallService.BuildPatchKey(other);
        Check("不同文件的补丁键不同", key1 != key3, $"{key1} / {key3}");

        var history = new List<InstallRecord>
        {
            new()
            {
                PatchKey = key1,
                GameId = "g",
                GameName = "G",
                Status = InstallStatus.Success,
            },
        };

        var isDuplicate = history.Any(h =>
            h.Status == InstallStatus.Success &&
            string.Equals(h.PatchKey, key1, StringComparison.OrdinalIgnoreCase));

        Check("相同补丁键在历史中可识别为重复", isDuplicate);

        var notDuplicate = history.Any(h =>
            h.Status == InstallStatus.Success &&
            string.Equals(h.PatchKey, key3, StringComparison.OrdinalIgnoreCase));

        Check("不同补丁键不会被误判为重复", !notDuplicate);
    }

    private static void TestExeTrust()
    {
        Section("EXE 可信规则");

        var exePath = Path.Combine(Path.GetTempPath(), "galpatch-test-" + Guid.NewGuid().ToString("N")[..8] + ".exe");

        // 不写真实 PE，只用于哈希与规则判定
        File.WriteAllBytes(exePath, new byte[] { 0x4D, 0x5A, 0x90, 0x00, 0x03 });

        var hash = FileHash.Sha256(exePath);

        // 1) 没有任何规则 -> 不可信、不允许自动运行
        var settings = new AppSettings();
        settings.ApplyDefaults(Paths.AppDataRoot);

        var none = ExeTrustPolicy.Evaluate(exePath, hash, settings);
        Check("没有规则时判定为不可信", !none.IsTrusted, none.Reason);
        Check("没有规则时不允许自动运行", !none.AutoRunAllowed);

        // 2) 只有文件名通配，没有哈希/域名约束 -> 仍然不可信
        settings.ExeTrustRules.Add(new ExeTrustRule
        {
            Name = "只匹配名字",
            FileNamePattern = "*.exe",
        });

        var nameOnly = ExeTrustPolicy.Evaluate(exePath, hash, settings);
        Check("仅文件名匹配不足以判定可信", !nameOnly.IsTrusted, nameOnly.Reason);

        // 3) 哈希匹配但规则为交互式 -> 可信但不自动运行
        settings.ExeTrustRules.Clear();
        settings.ExeTrustRules.Add(new ExeTrustRule
        {
            Name = "哈希匹配-交互",
            FileNamePattern = "galpatch-test-*.exe",
            AllowedSha256 = { hash },
            Mode = ExeRunMode.Interactive,
        });

        var interactive = ExeTrustPolicy.Evaluate(exePath, hash, settings);
        Check("哈希匹配后判定为可信", interactive.IsTrusted, interactive.Reason);
        Check("交互式规则不允许自动运行", !interactive.AutoRunAllowed);

        // 4) 哈希匹配 + 静默参数，但默认要求确认 -> 仍不自动运行
        settings.ExeTrustRules.Clear();
        settings.ExeTrustRules.Add(new ExeTrustRule
        {
            Name = "哈希匹配-静默",
            FileNamePattern = "*.exe",
            AllowedSha256 = { hash },
            SilentArguments = "/S",
            Mode = ExeRunMode.Silent,
        });

        var silent = ExeTrustPolicy.Evaluate(exePath, hash, settings);
        Check("静默规则命中", silent.IsTrusted && silent.HasSilentArguments, silent.Reason);
        Check("默认仍要求确认，不自动运行", !silent.AutoRunAllowed);

        // 5) 显式开启自动运行 + 关闭强制确认 -> 才允许自动运行
        settings.AlwaysConfirmExe = false;
        settings.AutoRunTrustedExe = true;

        var auto = ExeTrustPolicy.Evaluate(exePath, hash, settings);
        Check("显式开启后才允许自动运行", auto.AutoRunAllowed, auto.Reason);

        // 6) 哈希不匹配 -> 不可信
        var wrongHash = ExeTrustPolicy.Evaluate(exePath, new string('a', 64), settings);
        Check("哈希不匹配时不可信", !wrongHash.IsTrusted, wrongHash.Reason);

        // 7) 规则有静默模式但没有静默参数 -> 不自动运行
        settings.ExeTrustRules.Clear();
        settings.ExeTrustRules.Add(new ExeTrustRule
        {
            Name = "静默但无参数",
            FileNamePattern = "*.exe",
            AllowedSha256 = { hash },
            Mode = ExeRunMode.Silent,
        });

        var noArgs = ExeTrustPolicy.Evaluate(exePath, hash, settings);
        Check("规则未配置静默参数时不自动运行", !noArgs.AutoRunAllowed, noArgs.Reason);

        // 8) 非 exe 文件不允许通过运行器
        var txt = Path.Combine(Path.GetTempPath(), "not-an-exe.txt");
        File.WriteAllText(txt, "x");

        var runResult = ExePatchRunner.RunAsync(txt, null, false).GetAwaiter().GetResult();
        Check("运行器拒绝非 exe 文件", !runResult.Started, runResult.Message);

        TryDelete(exePath);
        TryDelete(txt);
    }

    private static void TestPathGuard()
    {
        Section("路径安全");

        var root = Path.Combine(Path.GetTempPath(), "galpatch-guard");

        var cases = new (string Path, bool ShouldPass)[]
        {
            ("normal/file.txt", true),
            ("sub/dir/file.txt", true),
            ("file.txt", true),
            ("../escape.txt", false),
            ("..\\escape.txt", false),
            ("a/../../escape.txt", false),
            ("/absolute.txt", false),
            ("C:/windows/system32/x.dll", false),
            ("\\\\server\\share\\x.txt", false),
            ("./file.txt", false),
        };

        foreach (var (path, shouldPass) in cases)
        {
            var ok = ArchiveService.TryGetSafeTarget(root, path, out _, out var reason);
            Check($"路径守卫「{path}」=> {(shouldPass ? "允许" : "拒绝")}", ok == shouldPass,
                ok ? "被允许" : $"被拒绝({reason})");
        }
    }

    private static void TestSearchService()
    {
        Section("搜索服务");

        var settings = new AppSettings { SearchProvider = "Browser" };
        settings.ApplyDefaults(Paths.AppDataRoot);

        var service = new SearchService(settings);

        var queries = service.BuildQueries("千恋万花");

        Check("生成多个搜索关键词", queries.Count >= 3, string.Join(" | ", queries));
        Check("关键词包含游戏名", queries.All(q => q.Contains("千恋万花")));

        var urls = service.BuildBrowserUrls(queries);
        Check("生成浏览器搜索链接", urls.Count >= 4, urls.Count.ToString());
        Check("链接包含编码后的关键词",
            urls.Any(u => u.Contains("%E5%8D%83", StringComparison.OrdinalIgnoreCase)),
            urls.FirstOrDefault() ?? "");

        var outcome = service.SearchAsync("千恋万花", CancellationToken.None).GetAwaiter().GetResult();

        Check("浏览器模式下不伪造任何搜索结果", outcome.Candidates.Count == 0,
            outcome.Candidates.Count.ToString());
        Check("浏览器模式下提供备用链接", outcome.UsedBrowserFallback && outcome.BrowserUrls.Count > 0);
        Check("浏览器模式下给出说明", outcome.Message.Length > 0, outcome.Message);

        // SearXNG 未配置时应当明确报错并回退，而不是编造结果
        var searxSettings = new AppSettings { SearchProvider = "Searxng", SearxngBaseUrl = "" };
        searxSettings.ApplyDefaults(Paths.AppDataRoot);

        var searxOutcome = new SearchService(searxSettings)
            .SearchAsync("test", CancellationToken.None).GetAwaiter().GetResult();

        Check("SearXNG 未配置时明确报错", searxOutcome.IsError, searxOutcome.Message);
        Check("SearXNG 未配置时回退到浏览器链接",
            searxOutcome.UsedBrowserFallback && searxOutcome.BrowserUrls.Count > 0);
        Check("SearXNG 未配置时不编造结果", searxOutcome.Candidates.Count == 0);

        // 自定义接口缺少 {query} 占位符
        var customSettings = new AppSettings
        {
            SearchProvider = "CustomJson",
            CustomSearchUrlTemplate = "https://example.com/search?q=test",
        };
        customSettings.ApplyDefaults(Paths.AppDataRoot);

        var customOutcome = new SearchService(customSettings)
            .SearchAsync("test", CancellationToken.None).GetAwaiter().GetResult();

        Check("自定义接口缺少占位符时报错", customOutcome.IsError, customOutcome.Message);

        // 白名单标注
        var candidateSettings = new AppSettings { WhitelistedHosts = { "example.com" } };
        candidateSettings.ApplyDefaults(Paths.AppDataRoot);

        var candidateOutcome = new SearchOutcome();
        candidateOutcome.Candidates.Add(new PatchCandidate { Url = "https://example.com/patch" });
        candidateOutcome.Candidates.Add(new PatchCandidate { Url = "https://sub.example.com/patch2" });
        candidateOutcome.Candidates.Add(new PatchCandidate { Url = "https://other.com/patch3" });

        new SearchService(candidateSettings).FinalizeCandidates(candidateOutcome);

        Check("白名单域名被正确标注", candidateOutcome.Candidates[0].IsWhitelisted);
        Check("白名单域名的子域也被标注", candidateOutcome.Candidates[1].IsWhitelisted);
        Check("非白名单域名未被标注", !candidateOutcome.Candidates[2].IsWhitelisted);
        Check("候选结果永远不会被标记为已验证",
            candidateOutcome.Candidates.All(c => !c.IsVerified));
    }

    // ------------------------------------------------------------------ 工具

    private static void AddEntry(ZipArchive zip, string name, string content)
    {
        var entry = zip.CreateEntry(name);
        using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
        writer.Write(content);
    }

    private static void Section(string title)
    {
        Lines.Add("");
        Lines.Add($"--- {title} ---");
    }

    private static void Info(string message) => Lines.Add($"    · {message}");

    private static void Pass(string name)
    {
        _passed++;
        Lines.Add($"    [通过] {name}");
    }

    private static void Fail(string name, string detail)
    {
        _failed++;
        Lines.Add($"    [失败] {name}    {detail}");
    }

    private static void Check(string name, bool condition, string? detail = null)
    {
        if (condition)
        {
            Pass(name);
            return;
        }

        Fail(name, !string.IsNullOrEmpty(detail) ? detail : "(无详细信息)");
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
            // 自检清理失败可忽略
        }
    }
}

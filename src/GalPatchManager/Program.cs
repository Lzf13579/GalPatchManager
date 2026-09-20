using System.Text;
using System.Windows.Forms;
using GalPatchManager.Services;
using GalPatchManager.UI;

namespace GalPatchManager;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        // 提前准备目录，保证日志能写进去
        try
        {
            Paths.EnsureCreated();
        }
        catch
        {
            // 极端情况下目录创建失败，后续 ConfigStore 会再试
        }

        // 自检模式：验证核心逻辑，不启动界面
        if (args.Any(a => string.Equals(a, "--selftest", StringComparison.OrdinalIgnoreCase)))
        {
            Environment.ExitCode = SelfTest.Run();
            return;
        }

        // 网络连通性测试：验证能否访问 Steam 商店接口（用于排查代理环境）
        if (args.Any(a => string.Equals(a, "--nettest", StringComparison.OrdinalIgnoreCase)))
        {
            Environment.ExitCode = RunNetTest();
            return;
        }

        // 命令行归类：不开界面，直接跑一次自动归类并把报告写到 classify-result.txt
        if (args.Any(a => string.Equals(a, "--classify", StringComparison.OrdinalIgnoreCase)))
        {
            Environment.ExitCode = RunClassify();
            return;
        }

        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        // 全局异常处理：任何未捕获异常都写入日志并提示用户，避免静默崩溃
        Application.ThreadException += (_, e) => HandleFatal(e.Exception, "界面线程");
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            HandleFatal(e.ExceptionObject as Exception, "后台线程");
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            HandleFatal(e.Exception, "未观察的任务");
            e.SetObserved();
        };

        Log.Info("================ 游戏补丁安装器启动 ================");
        Log.Info($"版本: {typeof(Program).Assembly.GetName().Version}");
        Log.Info($"操作系统: {Environment.OSVersion}");
        Log.Info($"运行时: {Environment.Version}");

        // 清理超过 3 天的临时解压目录
        Paths.CleanTempQuietly(TimeSpan.FromDays(3));

        try
        {
            using var context = new UI.AppContext();

            // 顺手清理过期备份
            context.Installer.CleanupOldBackups();

            // --tab=设置 / --tab=downloads 可以指定启动时显示的页面
            string? initialPage = null;

            foreach (var arg in args)
            {
                if (arg.StartsWith("--tab=", StringComparison.OrdinalIgnoreCase))
                {
                    initialPage = arg["--tab=".Length..];
                    break;
                }
            }

            Application.Run(new FormMain(context, initialPage));
        }
        catch (Exception ex)
        {
            HandleFatal(ex, "启动");
        }

        Log.Info("================ 游戏补丁安装器退出 ================");
    }

    /// <summary>
    /// 网络连通性测试：尝试访问 Steam 商店接口，把结果写入 nettest.txt。
    /// 用于排查"能不能抓到元数据"，不影响正常功能。
    /// </summary>
    private static int RunNetTest()
    {
        var report = new System.Text.StringBuilder();

        try
        {
            Paths.EnsureCreated();

            report.AppendLine($"时间: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            report.AppendLine($"系统代理: {System.Net.WebRequest.GetSystemWebProxy()?.GetProxy(new Uri("https://store.steampowered.com"))}");
            report.AppendLine();

            // 用一个确定存在的 AppID 做测试
            var settings = ConfigStore.LoadSettings();

            report.AppendLine($"联网方式: {settings.NetworkMode}");
            report.AppendLine();

            SteamMetadataService.ResetProbe();

            var strategy = SteamMetadataService.ProbeAsync(settings, CancellationToken.None)
                .GetAwaiter().GetResult();

            report.AppendLine(strategy is null
                ? "[探测] 所有网络策略均失败"
                : $"[探测] 可用策略: {strategy}");

            report.AppendLine();

            foreach (var appId in new[] { "2845270", "228980" })
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();

                var meta = SteamMetadataService.FetchAsync(appId, settings, CancellationToken.None)
                    .GetAwaiter().GetResult();

                sw.Stop();

                if (meta is null)
                {
                    report.AppendLine($"[失败] appid={appId} ({sw.ElapsedMilliseconds} ms)");
                }
                else
                {
                    report.AppendLine($"[成功] appid={appId} ({sw.ElapsedMilliseconds} ms)");
                    report.AppendLine($"        名称: {meta.Name}");
                    report.AppendLine($"        genres: {string.Join(" | ", meta.Genres)}");
                    report.AppendLine($"        categories: {string.Join(" | ", meta.Categories)}");
                }
            }
        }
        catch (Exception ex)
        {
            report.AppendLine($"[异常] {ex.GetType().Name}: {ex.Message}");
        }

        var text = report.ToString();

        try
        {
            File.WriteAllText(
                Path.Combine(Paths.AppDataRoot, "nettest.txt"), text, new System.Text.UTF8Encoding(false));
        }
        catch
        {
            // 报告写不出来也不影响退出码
        }

        return text.Contains("[成功]", StringComparison.Ordinal) ? 0 : 1;
    }

    /// <summary>
    /// 命令行归类：不开界面，跑一次自动归类，报告写入 classify-result.txt。
    /// 便于排查联网/归类问题，也方便重复执行。
    /// </summary>
    private static int RunClassify()
    {
        var report = new System.Text.StringBuilder();

        try
        {
            Paths.EnsureCreated();

            using var context = new UI.AppContext();

            var games = context.Library.RefreshSteamAsync().GetAwaiter().GetResult();

            var appIdCount = games.Count(g => !string.IsNullOrWhiteSpace(g.AppId));

            report.AppendLine($"时间: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            report.AppendLine($"游戏总数: {games.Count}（有 AppID {appIdCount}）");
            report.AppendLine($"联网方式: {context.Settings.NetworkMode}");
            report.AppendLine();

            var allowNetwork = false;

            if (context.Settings.EnableSteamMetadata && appIdCount > 0)
            {
                SteamMetadataService.ResetProbe();

                var strategy = SteamMetadataService
                    .ProbeAsync(context.Settings, CancellationToken.None).GetAwaiter().GetResult();

                report.AppendLine(strategy is null
                    ? "[探测] 所有网络策略均失败，本次只用本地规则"
                    : $"[探测] 可用策略: {strategy}");

                allowNetwork = strategy is not null;
            }

            report.AppendLine();

            var classifyReport = new ClassifyReport();
            var classifier = new AutoClassifier(context.Settings);

            foreach (var game in games)
            {
                try
                {
                    classifier.ClassifyAsync(game, classifyReport, allowNetwork)
                        .GetAwaiter().GetResult();

                    classifier.Apply(game, classifyReport);
                }
                catch (Exception ex)
                {
                    Log.Warn($"归类失败 {game.Name}: {ex.Message}");
                }
            }

            TagCache.Save();
            context.Settings.ApplyDefaults(Paths.AppDataRoot);
            ConfigStore.SaveSettings(context.Settings);

            report.AppendLine("=== 归类结果 ===");
            report.AppendLine($"Steam 社区标签归类: {classifyReport.FromTag}（其中本次新抓标签 {classifyReport.FromTagNetwork}）");
            report.AppendLine($"Steam 官方类型映射: {classifyReport.FromGenre}");
            report.AppendLine($"本地引擎特征识别 : {classifyReport.FromHeuristic}");
            report.AppendLine($"游戏名关键词     : {classifyReport.FromName}");
            report.AppendLine($"跳过（手动指定） : {classifyReport.SkippedManual}");
            report.AppendLine($"仍为未分类       : {classifyReport.Unclassified}");
            report.AppendLine($"元数据 命中缓存  : {classifyReport.FromCache}，新抓取 {classifyReport.FromNetwork}，"
                                + $"失败 {classifyReport.NetworkFailures}");
            report.AppendLine();

            // 社区标签命中统计
            var withTags = 0;
            var visualNovel = 0;

            foreach (var game in games)
            {
                var info = TagCache.Get(GameTypeCatalog.BuildStableKey(game));

                if (info is null || info.CommunityTags.Count == 0) continue;

                withTags++;

                if (info.CommunityTags.Any(t => t.Equals("Visual Novel", StringComparison.OrdinalIgnoreCase)))
                {
                    visualNovel++;
                }
            }

            report.AppendLine("=== 社区标签命中 ===");
            report.AppendLine($"抓到社区标签的游戏: {withTags} / {games.Count}");
            report.AppendLine($"带「Visual Novel」标签: {visualNovel}");
            report.AppendLine();

            // 库标签检测汇总
            report.AppendLine("=== 库标签检测 ===");

            var nativeChinese = 0;
            var noChinese = 0;
            var notChecked = 0;

            foreach (var game in games)
            {
                var info = TagCache.Get(GameTypeCatalog.BuildStableKey(game));

                if (info is null || string.IsNullOrWhiteSpace(info.SupportedLanguages)) notChecked++;
                else if (LibraryStatus.DetectChinese(info.SupportedLanguages)) nativeChinese++;
                else noChinese++;
            }

            report.AppendLine($"原生支持中文: {nativeChinese}");
            report.AppendLine($"无中文（可能需要汉化补丁）: {noChinese}");
            report.AppendLine($"未检测到语言信息: {notChecked}");
            report.AppendLine();

            report.AppendLine("=== 明细（每个游戏）===");

            foreach (var game in games.OrderBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase))
            {
                var info = TagCache.Get(GameTypeCatalog.BuildStableKey(game));

                var type = GameTypeCatalog.ResolveType(game, context.Settings);
                var status = LibraryStatus.Describe(info, 0, 0);

                report.AppendLine($"{game.Name}");
                report.AppendLine($"    类型: {type}   库检测: {status}");

                if (info is not null && info.Genres.Count > 0)
                {
                    report.AppendLine($"    genres: {string.Join("/", info.Genres)}");
                }

                if (!string.IsNullOrWhiteSpace(game.DetectedTypeReason))
                {
                    report.AppendLine($"    归类依据: {game.DetectedTypeReason}");
                }
            }

            report.AppendLine();
            report.AppendLine($"缓存条目数: {TagCache.Count}");
        }
        catch (Exception ex)
        {
            report.AppendLine($"[异常] {ex.GetType().Name}: {ex.Message}");
            report.AppendLine(ex.ToString());
        }

        var text = report.ToString();

        try
        {
            File.WriteAllText(
                Path.Combine(Paths.AppDataRoot, "classify-result.txt"), text, new System.Text.UTF8Encoding(false));
        }
        catch
        {
        }

        Log.Info("命令行归类完成，报告写入 classify-result.txt");

        return text.Contains("[异常]", StringComparison.Ordinal) ? 1 : 0;
    }

    private static void HandleFatal(Exception? ex, string source)    {
        if (ex is null) return;

        var text = new StringBuilder()
            .AppendLine($"来源: {source}")
            .AppendLine($"类型: {ex.GetType().FullName}")
            .AppendLine($"消息: {ex.Message}")
            .AppendLine()
            .AppendLine(ex.ToString())
            .ToString();

        Log.Error($"未处理的异常（{source}）: {ex.Message}");
        Log.Error(text);

        try
        {
            MessageBox.Show(
                $"程序遇到未处理的错误：\r\n\r\n{ex.Message}\r\n\r\n"
                + $"详细信息已写入日志：\r\n{Paths.TodayLogFile}",
                "游戏补丁安装器 - 错误",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
        catch
        {
            // 连弹窗都失败时就不要继续抛了
        }
    }
}

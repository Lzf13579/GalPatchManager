using System.Text;
using System.Text.RegularExpressions;
using GalPatchManager.Models;

namespace GalPatchManager.Services;

/// <summary>一次匹配的结论。</summary>
public sealed class MatchResult
{
    public GameEntry? Game { get; init; }

    /// <summary>0~1 的置信度。</summary>
    public double Score { get; init; }

    /// <summary>可读的匹配依据，展示给用户。</summary>
    public string Reason { get; init; } = "";

    public bool IsConfident => Game is not null && Score >= 0.55;
}

/// <summary>
/// 把补丁文件名映射到游戏的匹配器。
/// 顺序：AppID 命中 → 用户关键词规则 → 名称模糊匹配。
/// 任何一步都不确定时返回置信度较低的结果，由界面让用户确认。
/// </summary>
public static class NameMatcher
{
    /// <summary>常见无意义后缀/标签，参与匹配时剔除。</summary>
    private static readonly string[] NoiseWords =
    {
        "汉化", "汉化版", "中文", "中文版", "简体", "简体中文", "繁体", "补丁", "補丁", "patch", "crack",
        "fix", "update", "updateonly", "v", "ver", "version", "final", "repack", "setup", "installer",
        "hd", "full", "win", "windows", "x64", "x86", "pc", "chinese", "cht", "chs", "jpn", "jp",
        "uncensored", "censored", "eng", "english", "nodvd", "cracked", "dlc",
    };

    private static readonly Regex BracketRegex = new(
        @"[\[\(\{【（〔《<].*?[\]\)\}】）〕》>]", RegexOptions.Compiled | RegexOptions.Singleline);

    private static readonly Regex MultiSpaceRegex = new(@"\s+", RegexOptions.Compiled);

    /// <summary>允许的补丁文件扩展名（与设置中的默认值保持一致）。</summary>
    private static readonly string[] DefaultPatchExtensions = { "zip", "7z", "rar", "exe" };

    /// <summary>
    /// 规范化名称：
    /// 去括号内容、全角转半角、平假名转片假名、去掉空白与标点、剔除常见噪声词。
    /// </summary>
    public static string Normalize(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "";

        var text = name.Normalize(NormalizationForm.FormKC);

        // 去掉括号及其中内容（通常是 [汉化组] 【Ver1.02】 之类）
        text = BracketRegex.Replace(text, " ");

        var sb = new StringBuilder(text.Length);

        foreach (var ch in text)
        {
            // 平假名 -> 片假名，统一假名写法
            if (ch >= '\u3041' && ch <= '\u3096')
            {
                sb.Append((char)(ch + 0x60));
                continue;
            }

            if (char.IsLetterOrDigit(ch) || IsCjk(ch) || ch == ' ')
            {
                sb.Append(ch);
            }
            else
            {
                sb.Append(' ');
            }
        }

        var lowered = sb.ToString().ToLowerInvariant();
        lowered = MultiSpaceRegex.Replace(lowered, " ").Trim();

        // 逐词剔除噪声
        var words = lowered
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(w => !NoiseWords.Contains(w))
            .ToArray();

        return string.Join(" ", words);
    }

    /// <summary>去掉所有空白，便于 CJK 场景下做包含判断。</summary>
    public static string Compact(string? normalized)
    {
        if (string.IsNullOrEmpty(normalized)) return "";
        return normalized.Replace(" ", "", StringComparison.Ordinal);
    }

    /// <summary>
    /// 从文件名（或压缩包内路径）中提取用于匹配的有效部分。
    /// </summary>
    public static string FileNameToProbe(string fileNameOrPath, IEnumerable<string>? patchExtensions = null)
    {
        var value = fileNameOrPath;

        // 只取最后一段，避免把上层目录名混进来
        var slash = value.LastIndexOfAny(new[] { '\\', '/' });

        if (slash >= 0 && slash < value.Length - 1) value = value[(slash + 1)..];

        var exts = (patchExtensions ?? DefaultPatchExtensions)
            .Select(x => x.Trim().TrimStart('.').ToLowerInvariant())
            .Where(x => x.Length > 0)
            .ToArray();

        var current = value;

        // 可能有多层临时后缀，例如 "xxx.zip.tmp"
        for (var i = 0; i < 3; i++)
        {
            var ext = Path.GetExtension(current);

            if (string.IsNullOrEmpty(ext)) break;

            var bare = ext.TrimStart('.').ToLowerInvariant();

            if (exts.Contains(bare) || new[] { "tmp", "part", "crdownload" }.Contains(bare))
            {
                current = Path.GetFileNameWithoutExtension(current);
                continue;
            }

            break;
        }

        return current;
    }

    /// <summary>尝试从文件名/路径中找到 Steam AppID。</summary>
    public static string? ExtractAppId(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        // appmanifest_1234 或 "app 1234" / "appid=1234" / "[1234]"
        var patterns = new[]
        {
            @"appmanifest_(\d{2,10})",
            @"appid[\s_\-:=]*(\d{2,10})",
            @"app[\s_\-:=]+(\d{2,10})",
            @"\[(\d{4,10})\]",
            @"\((\d{4,10})\)",
        };

        foreach (var p in patterns)
        {
            var m = Regex.Match(text, p, RegexOptions.IgnoreCase);
            if (m.Success) return m.Groups[1].Value;
        }

        return null;
    }

    /// <summary>用 AppID 匹配游戏。</summary>
    public static MatchResult? MatchByAppId(string fileNameOrPath, IReadOnlyList<GameEntry> games)
    {
        var appId = ExtractAppId(fileNameOrPath);
        if (appId is null) return null;

        var game = games.FirstOrDefault(g =>
            !string.IsNullOrEmpty(g.AppId) &&
            string.Equals(g.AppId, appId, StringComparison.OrdinalIgnoreCase));

        if (game is null) return null;

        return new MatchResult
        {
            Game = game,
            Score = 1.0,
            Reason = $"文件名中的 AppID {appId} 命中游戏",
        };
    }

    /// <summary>用用户配置的关键词规则匹配。</summary>
    public static MatchResult? MatchByKeywords(
        string fileNameOrPath,
        IReadOnlyList<GameEntry> games,
        IReadOnlyList<MatchRuleEntry> rules)
    {
        var haystack = fileNameOrPath.ToLowerInvariant();
        var haystackCompact = Compact(Normalize(fileNameOrPath));
        var best = new MatchResult { Score = 0 };

        // 1) 用户显式规则优先
        foreach (var rule in rules.Where(r => r.Enabled && !string.IsNullOrWhiteSpace(r.Keyword)))
        {
            var keyword = rule.Keyword.ToLowerInvariant();

            if (!haystack.Contains(keyword, StringComparison.Ordinal) &&
                !haystackCompact.Contains(Compact(Normalize(keyword)), StringComparison.Ordinal))
            {
                continue;
            }

            var target = games.FirstOrDefault(g => g.Id == rule.GameId);
            if (target is null) continue;

            // 关键词越长越可信
            var score = Math.Min(0.95, 0.6 + keyword.Length * 0.02);

            if (score > best.Score)
            {
                best = new MatchResult
                {
                    Game = target,
                    Score = score,
                    Reason = $"用户规则命中关键词「{rule.Keyword}」",
                };
            }
        }

        if (best.Game is not null) return best;

        // 2) 游戏自身配置的关键词
        foreach (var game in games)
        {
            foreach (var keyword in game.MatchKeywords.Where(k => !string.IsNullOrWhiteSpace(k)))
            {
                var k = keyword.ToLowerInvariant();

                if (!haystack.Contains(k, StringComparison.Ordinal) &&
                    !haystackCompact.Contains(Compact(Normalize(k)), StringComparison.Ordinal))
                {
                    continue;
                }

                var score = Math.Min(0.95, 0.6 + k.Length * 0.02);

                if (score > best.Score)
                {
                    best = new MatchResult
                    {
                        Game = game,
                        Score = score,
                        Reason = $"命中游戏关键词「{keyword}」",
                    };
                }
            }
        }

        return best.Game is null ? null : best;
    }

    /// <summary>名称模糊匹配（CJK 用二元组，拉丁用词重合）。</summary>
    public static MatchResult MatchByFuzzy(string fileNameOrPath, IReadOnlyList<GameEntry> games)
    {
        var probe = Compact(Normalize(FileNameToProbe(fileNameOrPath)));

        if (probe.Length == 0) return new MatchResult { Score = 0, Reason = "文件名为空" };

        var best = new MatchResult { Score = 0 };

        foreach (var game in games)
        {
            var nameCompact = Compact(string.IsNullOrEmpty(game.NormalizedName)
                ? Normalize(game.Name)
                : game.NormalizedName);

            if (nameCompact.Length < 2) continue;

            double score;
            string reason;

            if (probe.Contains(nameCompact, StringComparison.Ordinal))
            {
                // 文件名完整包含游戏名，最强信号
                score = 0.9;
                reason = "文件名完整包含游戏名";
            }
            else if (nameCompact.Contains(probe, StringComparison.Ordinal) && probe.Length >= 4)
            {
                score = 0.7;
                reason = "游戏名包含文件名主体";
            }
            else
            {
                // 二元组重合度
                var nameGrams = Bigrams(nameCompact);
                var probeGrams = Bigrams(probe);

                if (nameGrams.Count == 0 || probeGrams.Count == 0) continue;

                var hit = nameGrams.Count(g => probeGrams.Contains(g));
                var ratio = (double)hit / nameGrams.Count;

                score = ratio * 0.85;
                reason = $"名称相似度 {ratio:P0}（{hit}/{nameGrams.Count} 词组）";
            }

            if (score > best.Score)
            {
                best = new MatchResult { Game = game, Score = score, Reason = reason };
            }
        }

        return best;
    }

    /// <summary>完整匹配流程。</summary>
    public static MatchResult FindMatch(
        string fileNameOrPath,
        IReadOnlyList<GameEntry> games,
        IReadOnlyList<MatchRuleEntry>? rules = null)
    {
        if (games.Count == 0) return new MatchResult { Score = 0, Reason = "没有可匹配的游戏" };

        var byAppId = MatchByAppId(fileNameOrPath, games);
        if (byAppId is not null) return byAppId;

        var byKeyword = MatchByKeywords(fileNameOrPath, games, rules ?? Array.Empty<MatchRuleEntry>());
        if (byKeyword is not null) return byKeyword;

        return MatchByFuzzy(fileNameOrPath, games);
    }

    private static HashSet<string> Bigrams(string text)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);

        if (text.Length == 1)
        {
            set.Add(text);
            return set;
        }

        for (var i = 0; i + 1 < text.Length; i++)
        {
            set.Add(text.Substring(i, 2));
        }

        return set;
    }

    private static bool IsCjk(char ch) =>
        (ch >= '\u4e00' && ch <= '\u9fff') ||   // CJK 统一表意
        (ch >= '\u3040' && ch <= '\u30ff') ||   // 假名
        (ch >= '\u3400' && ch <= '\u4dbf') ||   // 扩展 A
        (ch >= '\uf900' && ch <= '\ufaff');     // 兼容表意
}

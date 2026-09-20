using GalPatchManager.Models;

namespace GalPatchManager.Services;

/// <summary>
/// 游戏库：合并 Steam 自动识别结果与用户手动添加的游戏，并负责持久化手动条目与关键词规则。
/// </summary>
public sealed class GameLibrary
{
    private readonly AppSettings _settings;

    /// <summary>最近一次 Steam 扫描结果。</summary>
    private readonly List<GameEntry> _steamGames = new();

    public GameLibrary(AppSettings settings)
    {
        _settings = settings;
    }

    public IReadOnlyList<GameEntry> SteamGames => _steamGames;

    public List<string> SteamDiagnostics { get; } = new();

    public string? SteamRoot { get; private set; }

    public List<string> SteamLibraryPaths { get; } = new();

    /// <summary>合并后的全部游戏（Steam + 手动），已应用隐藏列表。</summary>
    public List<GameEntry> AllGames
    {
        get
        {
            var list = new List<GameEntry>();

            list.AddRange(_steamGames.Where(g =>
                string.IsNullOrEmpty(g.AppId) ||
                !_settings.HiddenAppIds.Contains(g.AppId!, StringComparer.OrdinalIgnoreCase)));

            list.AddRange(_settings.ManualGames);

            return list
                .OrderBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
        }
    }

    /// <summary>执行 Steam 扫描并刷新内部缓存。</summary>
    public async Task<List<GameEntry>> RefreshSteamAsync(CancellationToken cancellationToken = default)
    {
        var detector = new SteamDetector();

        var games = await detector.DetectAsync(_settings.ExtraSteamLibraryPaths, cancellationToken)
            .ConfigureAwait(false);

        _steamGames.Clear();
        _steamGames.AddRange(games);

        SteamDiagnostics.Clear();
        SteamDiagnostics.AddRange(detector.Diagnostics);

        SteamRoot = detector.SteamRoot;

        SteamLibraryPaths.Clear();
        SteamLibraryPaths.AddRange(detector.LibraryPaths);

        if (detector.SteamRoot is null)
        {
            Log.Warn("未找到 Steam，仅显示手动添加的游戏。");
        }

        return AllGames;
    }

    public GameEntry? FindById(string? id)
    {
        if (string.IsNullOrEmpty(id)) return null;

        return AllGames.FirstOrDefault(g => g.Id == id);
    }

    /// <summary>添加手动游戏。</summary>
    public GameEntry AddManual(string name, string installDir, IEnumerable<string>? keywords = null)
    {
        var entry = new GameEntry
        {
            Name = name.Trim(),
            NormalizedName = NameMatcher.Normalize(name),
            InstallDir = Path.GetFullPath(installDir.Trim()),
            Source = GameSource.Manual,
            Note = "用户手动添加",
            MatchKeywords = keywords?.Where(k => !string.IsNullOrWhiteSpace(k)).ToList() ?? new List<string>(),
        };

        _settings.ManualGames.Add(entry);
        ConfigStore.SaveSettings(_settings);

        Log.Info($"已添加手动游戏: {entry.Name} -> {entry.InstallDir}");
        return entry;
    }

    /// <summary>移除手动游戏。Steam 游戏只能隐藏，不能删除。</summary>
    public bool Remove(GameEntry game)
    {
        if (game.Source == GameSource.Steam)
        {
            if (!string.IsNullOrEmpty(game.AppId) &&
                !_settings.HiddenAppIds.Contains(game.AppId!, StringComparer.OrdinalIgnoreCase))
            {
                _settings.HiddenAppIds.Add(game.AppId!);
                ConfigStore.SaveSettings(_settings);
                Log.Info($"已从列表隐藏 Steam 游戏: {game.Name} ({game.AppId})");
                return true;
            }

            return false;
        }

        var removed = _settings.ManualGames.RemoveAll(g => g.Id == game.Id) > 0;

        if (removed)
        {
            ConfigStore.SaveSettings(_settings);
            Log.Info($"已移除手动游戏: {game.Name}");
        }

        return removed;
    }

    /// <summary>修正识别结果（改名 / 改目录 / 改关键词）。</summary>
    public void UpdateGame(GameEntry game, string newName, string newDir, IEnumerable<string>? keywords = null)
    {
        game.Name = newName.Trim();
        game.NormalizedName = NameMatcher.Normalize(newName);
        game.InstallDir = Path.GetFullPath(newDir.Trim());

        if (keywords is not null)
        {
            game.MatchKeywords = keywords.Where(k => !string.IsNullOrWhiteSpace(k)).ToList();
        }

        // Steam 游戏的修正需要持久化：把它复制成手动条目，并隐藏原始 Steam 条目
        if (game.Source == GameSource.Steam)
        {
            if (!string.IsNullOrEmpty(game.AppId) &&
                !_settings.HiddenAppIds.Contains(game.AppId!, StringComparer.OrdinalIgnoreCase))
            {
                _settings.HiddenAppIds.Add(game.AppId!);
            }

            var copy = new GameEntry
            {
                Name = game.Name,
                NormalizedName = game.NormalizedName,
                AppId = game.AppId,
                InstallDir = game.InstallDir,
                Source = GameSource.Manual,
                Note = "（由 Steam 识别结果修正而来）",
                MatchKeywords = game.MatchKeywords,
            };

            _settings.ManualGames.RemoveAll(g => g.AppId == copy.AppId && !string.IsNullOrEmpty(copy.AppId));
            _settings.ManualGames.Add(copy);
        }

        ConfigStore.SaveSettings(_settings);
        Log.Info($"已更新游戏信息: {game.Name} -> {game.InstallDir}");
    }

    /// <summary>搜索过滤。</summary>
    public List<GameEntry> Search(IEnumerable<GameEntry> source, string? keyword)
    {
        var list = source.ToList();

        if (string.IsNullOrWhiteSpace(keyword)) return list;

        var k = keyword.Trim();

        return list.Where(g =>
                g.Name.Contains(k, StringComparison.CurrentCultureIgnoreCase) ||
                g.InstallDir.Contains(k, StringComparison.OrdinalIgnoreCase) ||
                (!string.IsNullOrEmpty(g.AppId) &&
                 g.AppId!.Contains(k, StringComparison.OrdinalIgnoreCase)))
            .ToList();
    }

    public void Save() => ConfigStore.SaveSettings(_settings);
}

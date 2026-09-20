using GalPatchManager.Models;
using GalPatchManager.Services;

namespace GalPatchManager.UI;

/// <summary>
/// 应用级上下文：持有设置、游戏库、待处理补丁、安装历史、下载监控等共享状态。
/// 参考 PCL 的做法，把核心服务集中在一处，界面各页面按需取用。
/// </summary>
public sealed class AppContext : IDisposable
{
    public AppSettings Settings { get; }

    public GameLibrary Library { get; }

    public InstallService Installer { get; }

    public SearchService Search { get; }

    public DownloadMonitor Monitor { get; }

    /// <summary>安装历史（内存副本，落盘到 history.json）。</summary>
    public List<InstallRecord> History { get; }

    /// <summary>待处理补丁（内存副本，落盘到 downloads.json）。</summary>
    public List<PatchDownloadItem> Pending { get; }

    /// <summary>最近一次搜索的候选网址。</summary>
    public List<PatchCandidate> LastCandidates { get; } = new();

    /// <summary>最近一次搜索的浏览器备用链接。</summary>
    public List<string> LastBrowserUrls { get; } = new();

    /// <summary>待处理补丁发生变化。</summary>
    public event EventHandler? PendingChanged;

    /// <summary>安装历史发生变化。</summary>
    public event EventHandler? HistoryChanged;

    public AppContext()
    {
        Paths.EnsureCreated();

        Settings = ConfigStore.LoadSettings();
        Library = new GameLibrary(Settings);
        Installer = new InstallService(Settings);
        Search = new SearchService(Settings);
        Monitor = new DownloadMonitor(Settings);

        History = ConfigStore.LoadHistory();
        Pending = ConfigStore.LoadDownloads();

        // 重启后已完成的条目不应该再次触发安装
        foreach (var item in Pending.Where(p => p.State == PatchState.Completed || p.State == PatchState.Skipped))
        {
            item.IsComplete = true;
        }

        Monitor.PatchFileFound += OnPatchFileFound;

        Log.Info("程序启动。");
        Log.Info($"配置目录: {Paths.AppDataRoot}");
        Log.Info($"监控目录: {Settings.DownloadFolder}");
    }

    public void StartMonitor()
    {
        if (Settings.MonitorEnabled) Monitor.Start();
    }

    public void SaveAll()
    {
        ConfigStore.SaveSettings(Settings);
        ConfigStore.SaveHistory(History);
        ConfigStore.SaveDownloads(Pending);
    }

    public void NotifyPendingChanged()
    {
        ConfigStore.SaveDownloads(Pending);
        PendingChanged?.Invoke(this, EventArgs.Empty);
    }

    public void NotifyHistoryChanged()
    {
        ConfigStore.SaveHistory(History);
        HistoryChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>把一个“复制计划”转换成人可读的预览文本。</summary>
    public static string FormatPlan(IEnumerable<CopyStep> plan)
    {
        var lines = plan.Select(s =>
            s.IsBlocked
                ? $"[拒绝] {s.RelativeTarget} —— {s.BlockReason}"
                : $"[{s.ActionText}] {s.RelativeTarget}  ({s.SourceSize:N0} 字节)"
                  + (s.TargetExists ? "  ← 目标已存在，将先备份" : ""));

        return string.Join(Environment.NewLine, lines);
    }

    /// <summary>是否已经安装过同一个补丁（去重）。</summary>
    public bool IsAlreadyInstalled(string patchKey)
    {
        return History.Any(h =>
            h.Status == InstallStatus.Success &&
            string.Equals(h.PatchKey, patchKey, StringComparison.OrdinalIgnoreCase));
    }

    public InstallRecord? FindInstalledRecord(string patchKey)
    {
        return History.FirstOrDefault(h =>
            h.Status == InstallStatus.Success &&
            string.Equals(h.PatchKey, patchKey, StringComparison.OrdinalIgnoreCase));
    }

    private void OnPatchFileFound(object? sender, PatchFileFoundEventArgs e)
    {
        try
        {
            // 已经在待处理列表里的文件不重复添加
            var existing = Pending.FirstOrDefault(p =>
                string.Equals(p.FilePath, e.FilePath, StringComparison.OrdinalIgnoreCase));

            if (existing is not null)
            {
                existing.IsComplete = true;
                existing.Size = e.Size;
                existing.Sha256 = e.Sha256;
                existing.State = PatchState.Downloaded;
            }
            else
            {
                var item = new PatchDownloadItem
                {
                    FilePath = e.FilePath,
                    FileName = Path.GetFileName(e.FilePath),
                    Size = e.Size,
                    Sha256 = e.Sha256,
                    IsComplete = true,
                    State = PatchState.Downloaded,
                    IsExe = e.FilePath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase),
                };

                item.MatchedGameId = null;
                Pending.Add(item);
            }

            NotifyPendingChanged();
        }
        catch (Exception ex)
        {
            Log.Error("登记待处理补丁失败", ex);
        }
    }

    /// <summary>尝试为待处理条目自动匹配游戏。</summary>
    public void TryAutoMatch(PatchDownloadItem item)
    {
        var games = Library.AllGames;

        var result = NameMatcher.FindMatch(item.FileName, games, Settings.MatchRules);

        if (result.Game is not null && result.IsConfident)
        {
            item.MatchedGameId = result.Game.Id;
            item.MatchReason = $"{result.Reason}（置信度 {result.Score:P0}）";
            item.State = PatchState.Matched;
        }
        else if (result.Game is not null)
        {
            // 置信度不足：仍然记录候选，但状态保持“待确认”，由用户决定
            item.MatchedGameId = null;
            item.MatchReason = $"低置信度候选：{result.Game.Name}（{result.Reason}，{result.Score:P0}），请手动确认";
        }
        else
        {
            item.MatchReason = "未能自动匹配到游戏，请手动选择。";
        }

        item.IsDuplicate = IsAlreadyInstalled(InstallService.BuildPatchKey(item.FilePath));
        var record = FindInstalledRecord(InstallService.BuildPatchKey(item.FilePath));

        if (record is not null) item.DuplicateOfRecordId = record.Id;
    }

    public void Dispose()
    {
        Monitor.PatchFileFound -= OnPatchFileFound;
        Monitor.Dispose();
        SaveAll();
    }
}

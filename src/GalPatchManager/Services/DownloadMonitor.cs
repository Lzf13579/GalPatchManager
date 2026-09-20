using System.Collections.Concurrent;
using GalPatchManager.Models;

namespace GalPatchManager.Services;

/// <summary>监控到的新补丁文件。</summary>
public sealed class PatchFileFoundEventArgs : EventArgs
{
    public string FilePath { get; init; } = "";

    public long Size { get; init; }

    public string Sha256 { get; init; } = "";
}

/// <summary>文件状态变化的进度通知。</summary>
public sealed class PatchFileProgressEventArgs : EventArgs
{
    public string FilePath { get; init; } = "";

    public long Size { get; init; }

    public int StabilityChecks { get; init; }

    public int Required { get; init; }

    public string Message { get; init; } = "";
}

/// <summary>
/// 下载目录监控。
///
/// 判定“下载完成”的三重条件（缺一不可）：
///   1) 文件扩展名属于补丁类型，且不在忽略列表（.crdownload/.part/.tmp 等临时文件直接跳过）；
///   2) 文件大小连续 N 次轮询完全不变（N = StableChecksRequired，默认 3）；
///   3) 文件能够被完整读取（独占打开成功、可读到 EOF、长度与文件系统一致）。
/// </summary>
public sealed class DownloadMonitor : IDisposable
{
    private readonly AppSettings _settings;
    private readonly ConcurrentDictionary<string, TrackedFile> _tracked =
        new(StringComparer.OrdinalIgnoreCase);

    private CancellationTokenSource? _cts;
    private Task? _loop;

    /// <summary>确认下载完成（含 SHA-256）。</summary>
    public event EventHandler<PatchFileFoundEventArgs>? PatchFileFound;

    /// <summary>进度变化，供界面刷新状态列。</summary>
    public event EventHandler<PatchFileProgressEventArgs>? Progress;

    public DownloadMonitor(AppSettings settings)
    {
        _settings = settings;
    }

    public bool IsRunning => _loop is { IsCompleted: false };

    private sealed class TrackedFile
    {
        public long Size { get; set; } = -1;

        public DateTime LastWriteUtc { get; set; }

        public int StableCount { get; set; }

        /// <summary>大小开始保持不变的时刻，用于叠加“静默时长”判定。</summary>
        public DateTime? StableSinceUtc { get; set; }

        public bool Completed { get; set; }

        public string Sha256 { get; set; } = "";

        /// <summary>最近一次计算的内容哈希，用于发现“大小/时间戳都没变但内容变了”的情况。</summary>
        public string ContentHash { get; set; } = "";

        public DateTime FirstSeenUtc { get; set; } = DateTime.UtcNow;
    }

    // ------------------------------------------------------------------ 控制

    public void Start()
    {
        if (IsRunning) return;

        // 只处理新增文件时，确保监控目录已经有基线：
        // 第一次监控某个目录时，把目录里已有的文件全部登记为“已知”，一个都不处理。
        EnsureBaseline();

        _cts = new CancellationTokenSource();
        var token = _cts.Token;

        _loop = Task.Run(() => LoopAsync(token), token);

        Log.Info($"开始监控下载目录: {_settings.DownloadFolder}"
                 + $"（间隔 {_settings.PollIntervalSeconds}s，稳定判定 {_settings.StableChecksRequired} 次，"
                 + $"最少 {_settings.MinFileSizeMB} MB"
                 + (_settings.OnlyNewFiles
                     ? $"，仅处理新增文件，基线内 {IgnoreBaseline.CountOf(_settings.DownloadFolder)} 个已存在文件被忽略）"
                     : "，处理目录内全部匹配文件）"));
    }

    /// <summary>
    /// 确保监控目录有基线。调用方可在用户主动“重新建立基线”时直接调用。
    /// </summary>
    public void EnsureBaseline()
    {
        if (!_settings.OnlyNewFiles) return;

        var folder = _settings.DownloadFolder;

        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder)) return;

        if (IgnoreBaseline.HasBaseline(folder)) return;

        // 尚未建立基线：把当前已有文件全部登记，避免把历史文件当成新下载
        var count = IgnoreBaseline.CreateBaseline(folder);

        Log.Info($"首次监控该目录，已忽略其中已存在的 {count} 个文件（只处理之后新下载的补丁）");
    }

    public void Stop()
    {
        try
        {
            _cts?.Cancel();
        }
        catch
        {
            // 忽略
        }

        Log.Info("已停止下载目录监控。");
    }

    /// <summary>重启（设置变更后调用）。</summary>
    public void Restart()
    {
        Stop();
        _tracked.Clear();
        Start();
    }

    private async Task LoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                ScanOnce(token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                Log.Error("扫描下载目录时出错", ex);
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(_settings.PollIntervalSeconds), token)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    // ------------------------------------------------------------------ 扫描

    /// <summary>执行一次扫描（也便于单元测试直接调用）。</summary>
    public void ScanOnce(CancellationToken token = default)
    {
        var folder = _settings.DownloadFolder;

        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder)) return;

        string[] files;

        try
        {
            files = Directory.GetFiles(folder, "*", SearchOption.TopDirectoryOnly);
        }
        catch (Exception ex)
        {
            Log.Warn($"枚举下载目录失败: {ex.Message}");
            return;
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var file in files)
        {
            token.ThrowIfCancellationRequested();

            if (!IsCandidate(file)) continue;

            // 只要扩展名匹配就纳入跟踪，避免下面的过滤让跟踪表状态不一致
            seen.Add(file);

            // 体积过滤：太小的文件基本是 .url / 说明 / 空壳，直接跳过
            long length;

            DateTime lastWriteUtc;

            try
            {
                var info = new FileInfo(file);

                if (!info.Exists) continue;

                length = info.Length;
                lastWriteUtc = info.LastWriteTimeUtc;
            }
            catch (Exception ex)
            {
                Log.Debug($"跳过（读取文件信息失败）: {Path.GetFileName(file)} -> {ex.Message}");
                continue;
            }

            var minBytes = (long)Math.Max(0, _settings.MinFileSizeMB) * 1024 * 1024;

            if (minBytes > 0 && length < minBytes)
            {
                Log.Info($"忽略过小文件（{length:N0} 字节 < 下限 {minBytes:N0} 字节）: {Path.GetFileName(file)}");
                continue;
            }

            // 只处理新增/变化的文件：目录里早就在的文件不算“下载完成”
            if (_settings.OnlyNewFiles &&
                !IgnoreBaseline.IsNewOrChanged(folder, file, length, lastWriteUtc))
            {
                continue;
            }

            Log.Debug($"开始新文件评估: {Path.GetFileName(file)}（{length:N0} 字节）");

            // 注意：这里**不能**立刻把文件登记进基线。
            // 完成判定需要多轮扫描累积“稳定计数”，如果第一次扫描就登记为“已存在”，
            // 后续扫描会被基线过滤掉，文件永远无法判定完成（自检里有对应回归用例）。
            // 登记时机在 Evaluate 判定完成之后（见 MarkBaselineRecorded）。
            Evaluate(file, token);
        }


        // 清理已从目录中消失的跟踪项
        foreach (var key in _tracked.Keys.ToList())
        {
            if (!seen.Contains(key)) _tracked.TryRemove(key, out _);
        }
    }

    /// <summary>判断文件是否值得跟踪（忽略临时文件与无关类型）。</summary>
    public bool IsCandidate(string filePath)
    {
        var name = Path.GetFileName(filePath);

        if (string.IsNullOrEmpty(name)) return false;

        // 浏览器/下载器的隐藏临时名
        if (name.StartsWith('.') || name.StartsWith("~$", StringComparison.Ordinal)) return false;

        var lower = name.ToLowerInvariant();

        // 忽略 .crdownload / .part / .tmp 等：既看最终扩展名，也看整体后缀
        foreach (var suffix in _settings.IgnoredNameSuffixes)
        {
            if (string.IsNullOrWhiteSpace(suffix)) continue;

            if (lower.EndsWith(suffix.Trim().ToLowerInvariant(), StringComparison.Ordinal))
            {
                return false;
            }
        }

        if (!_settings.PatchExtensions.Contains("exe", StringComparer.OrdinalIgnoreCase) &&
            lower.EndsWith(".exe", StringComparison.Ordinal))
        {
            return false;
        }

        // 取最后一段扩展名判断
        var parts = lower.Split('.', StringSplitOptions.RemoveEmptyEntries);

        if (parts.Length < 2) return false;

        var lastExt = parts[^1];

        if (_settings.IgnoredExtensions.Contains(lastExt, StringComparer.Ordinal)) return false;

        if (!_settings.PatchExtensions.Contains(lastExt, StringComparer.Ordinal)) return false;

        // 形如 "xxx.zip.tmp" / "xxx.7z.part" 的中间态：最后一段是压缩包，但倒数第二段是临时后缀
        var secondLast = parts[^2];

        if (_settings.IgnoredExtensions.Contains(secondLast, StringComparer.Ordinal))
        {
            // 这种情况说明扩展名链里混了临时后缀，例如 a.tmp.zip，忽略以保守处理
            return false;
        }

        // 忽略本程序自己产生的下载状态/日志文件
        if (string.Equals(name, "downloads.json", StringComparison.OrdinalIgnoreCase)) return false;

        return true;
    }

    private void Evaluate(string filePath, CancellationToken token)
    {
        FileInfo info;

        try
        {
            info = new FileInfo(filePath);

            if (!info.Exists) return;

            // 文件仍被独占写入时，这里可能抛异常，视为“还在下载”
            _ = info.Length;
        }
        catch (Exception ex)
        {
            Log.Debug($"读取文件信息失败（视为未完成）: {filePath} -> {ex.Message}");
            return;
        }

        var tracked = _tracked.GetOrAdd(filePath, _ => new TrackedFile());

        if (tracked.Completed) return;

        var sizeChanged = tracked.Size != info.Length;
        var writeChanged = tracked.LastWriteUtc != info.LastWriteTimeUtc;

        if (sizeChanged || writeChanged)
        {
            tracked.Size = info.Length;
            tracked.LastWriteUtc = info.LastWriteTimeUtc;

            // 关键：任何变化都必须把稳定计数和稳定时长一起清零，
            // 否则“先变化几次、再静默几次”会被错误判定为完成。
            tracked.StableCount = 0;
            tracked.StableSinceUtc = null;

            Report(tracked, filePath, info.Length,
                $"检测到变化，重新计数（{info.Length:N0} 字节）");

            return;
        }

        tracked.StableSinceUtc ??= DateTime.UtcNow;
        tracked.StableCount++;

        var quietSeconds = (DateTime.UtcNow - tracked.StableSinceUtc.Value).TotalSeconds;
        var requiredQuiet = Math.Max(0, _settings.MinQuietSeconds);

        if (tracked.StableCount < _settings.StableChecksRequired)
        {
            Report(tracked, filePath, info.Length,
                $"大小稳定 {tracked.StableCount}/{_settings.StableChecksRequired}"
                + $"（{quietSeconds:F1}s）");

            return;
        }

        // 除了“连续 N 次没变”，还要求已经安静了足够长的时间
        if (requiredQuiet > 0 && quietSeconds < requiredQuiet)
        {
            Log.Debug($"等待静默时长: {Path.GetFileName(filePath)} {quietSeconds:F1}s / {requiredQuiet:F1}s");

            Report(tracked, filePath, info.Length,
                $"大小已稳定 {quietSeconds:F1}s / 需要 {requiredQuiet:F1}s");

            return;
        }

        Report(tracked, filePath, info.Length,
            $"大小稳定 {tracked.StableCount}/{_settings.StableChecksRequired}"
            + $"（已静默 {quietSeconds:F1}s）");

        // 大小稳定后，再确认文件可完整读取
        if (!FileHash.CanBeReadFully(filePath, out var reason))
        {
            tracked.StableCount = 0;
            tracked.StableSinceUtc = null;

            Log.Debug($"文件不可读，继续等待: {Path.GetFileName(filePath)} -> {reason}");

            Report(tracked, filePath, info.Length, $"大小稳定但文件不可读：{reason}");

            return;
        }

        // 压缩包再做一次可打开性校验；解压阶段还会做 CRC 校验
        if (ArchiveService.IsArchiveExtension(filePath) &&
            !ArchiveService.CanOpen(filePath, out var archiveError))
        {
            tracked.StableCount = 0;
            tracked.StableSinceUtc = null;

            Report(tracked, filePath, info.Length, $"压缩包暂时无法打开：{archiveError}");

            return;
        }

        token.ThrowIfCancellationRequested();

        string hash;

        try
        {
            hash = FileHash.Sha256Async(filePath, token).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            tracked.StableCount = 0;
            tracked.StableSinceUtc = null;

            Report(tracked, filePath, info.Length, $"计算校验值失败：{ex.Message}");

            return;
        }

        // 计算哈希期间文件可能又变了，必须复核一次，否则会把还在下载的文件当成完成
        FileInfo after;

        try
        {
            after = new FileInfo(filePath);
        }
        catch (Exception ex)
        {
            tracked.StableCount = 0;
            tracked.StableSinceUtc = null;

            Report(tracked, filePath, info.Length, $"复核文件状态失败：{ex.Message}");

            return;
        }

        if (!after.Exists || after.Length != info.Length || after.LastWriteTimeUtc != info.LastWriteTimeUtc)
        {
            tracked.Size = after.Exists ? after.Length : -1;
            tracked.LastWriteUtc = after.Exists ? after.LastWriteTimeUtc : default;
            tracked.StableCount = 0;
            tracked.StableSinceUtc = null;

            Report(tracked, filePath, info.Length, "计算校验值期间文件仍在变化，继续等待");

            return;
        }

        // 内容级去重：极少数情况下大小与时间戳都没变但内容变了，也要重新判定
        if (tracked.ContentHash.Length > 0 && tracked.ContentHash != hash)
        {
            tracked.ContentHash = hash;
            tracked.StableCount = 0;
            tracked.StableSinceUtc = null;

            Log.Debug($"内容变化，重新计数: {Path.GetFileName(filePath)}");

            Report(tracked, filePath, info.Length, "文件内容发生变化，重新计数");

            return;
        }

        tracked.Completed = true;
        tracked.Sha256 = hash;
        tracked.ContentHash = hash;

        // 判定完成后才登记进基线，保证后续扫描不会重复处理
        MarkBaselineRecorded(filePath, info.Length, info.LastWriteTimeUtc);

        Log.Debug($"判定下载完成: {Path.GetFileName(filePath)}（{info.Length:N0} 字节）");

        Report(tracked, filePath, info.Length, "下载完成");

        Log.Info($"补丁下载完成: {Path.GetFileName(filePath)}（{info.Length:N0} 字节，SHA-256 {hash[..Math.Min(16, hash.Length)]}…）");

        try
        {
            PatchFileFound?.Invoke(this, new PatchFileFoundEventArgs
            {
                FilePath = filePath,
                Size = info.Length,
                Sha256 = hash,
            });
        }
        catch (Exception ex)
        {
            Log.Error("处理下载完成事件失败", ex);
        }
    }

    /// <summary>
    /// 把一个已完成处理的文件登记进基线。
    /// 必须在“下载完成”或“确定忽略”之后调用，不能在第一次发现文件时就调用。
    /// </summary>
    private void MarkBaselineRecorded(string filePath, long size, DateTime lastWriteUtc)
    {
        if (!_settings.OnlyNewFiles) return;

        var folder = _settings.DownloadFolder;

        if (string.IsNullOrWhiteSpace(folder)) return;

        IgnoreBaseline.Record(folder, filePath, size, lastWriteUtc);
        IgnoreBaseline.SaveChanges();
    }

    private void Report(TrackedFile tracked, string filePath, long size, string message)
    {
        try
        {
            Progress?.Invoke(this, new PatchFileProgressEventArgs
            {
                FilePath = filePath,
                Size = size,
                StabilityChecks = tracked.StableCount,
                Required = _settings.StableChecksRequired,
                Message = message,
            });
        }
        catch (Exception ex)
        {
            Log.Debug($"进度事件处理失败: {ex.Message}");
        }
    }

    /// <summary>把文件标记为已处理（避免重启后重复触发）。</summary>
    public void MarkHandled(string filePath)
    {
        if (_tracked.TryGetValue(filePath, out var t)) t.Completed = true;
    }

    public void Dispose()
    {
        Stop();
        _cts?.Dispose();
        _cts = null;
    }
}

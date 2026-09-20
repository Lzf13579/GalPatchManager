using GalPatchManager.Models;

namespace GalPatchManager.Services;

/// <summary>一次安装的完整报告。</summary>
public sealed class InstallReport
{
    public bool Success { get; set; }

    public string GameId { get; set; } = "";

    public string GameName { get; set; } = "";

    public string PatchKey { get; set; } = "";

    public string PatchPath { get; set; } = "";

    public string Sha256 { get; set; } = "";

    public int FilesCopied { get; set; }

    public int FilesBackedUp { get; set; }

    public long BytesCopied { get; set; }

    public List<BackupEntry> Backups { get; } = new();

    public List<string> Steps { get; } = new();

    public string BackupBatchId { get; set; } = "";

    public string Error { get; set; } = "";

    /// <summary>是否发生了回滚。</summary>
    public bool RolledBack { get; set; }
}

/// <summary>
/// 一条“从哪儿复制到哪儿”的计划。源文件在解压目录中，目标文件在游戏目录中。
/// </summary>
public sealed class CopyStep
{
    public string SourcePath { get; init; } = "";

    public string TargetPath { get; init; } = "";

    /// <summary>相对游戏目录的路径，用于界面展示。</summary>
    public string RelativeTarget { get; init; } = "";

    public long SourceSize { get; init; }

    public bool TargetExists { get; init; }

    /// <summary>非空表示这一步有问题，安装会被拒绝。</summary>
    public string BlockReason { get; init; } = "";

    public bool IsBlocked => BlockReason.Length > 0;

    public string ActionText => IsBlocked ? "拒绝" : (TargetExists ? "覆盖" : "新增");
}

/// <summary>
/// 把解压出来的补丁文件按目录结构导入游戏目录。
///
/// 行为约定：
///   * 覆盖前自动备份到 %AppData%\GalPatchManager\Backups\&lt;批次&gt;\...
///   * 任何一步失败都记录具体错误，并根据设置自动回滚；失败绝不报告为成功。
///   * 只写目标目录内部，所有路径都经过穿越校验。
/// </summary>
public sealed class InstallService
{
    private readonly AppSettings _settings;

    public InstallService(AppSettings settings)
    {
        _settings = settings;
    }

    // ------------------------------------------------------------------ 计划

    /// <summary>
    /// 根据解压目录生成“将复制的文件”清单，供界面预览与安装使用。
    /// </summary>
    public List<CopyStep> PlanFromExtractDirectory(
        string extractRoot,
        string gameDir,
        IReadOnlyList<string>? files = null)
    {
        var steps = new List<CopyStep>();

        if (!Directory.Exists(extractRoot))
        {
            steps.Add(new CopyStep
            {
                BlockReason = $"解压目录不存在: {extractRoot}",
            });

            return steps;
        }

        IEnumerable<string> source = files is { Count: > 0 }
            ? files
            : Directory.EnumerateFiles(extractRoot, "*", SearchOption.AllDirectories);

        foreach (var file in source.OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
        {
            var relative = Path.GetRelativePath(extractRoot, file);

            if (relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative))
            {
                steps.Add(new CopyStep
                {
                    SourcePath = file,
                    RelativeTarget = relative,
                    BlockReason = "文件位于解压目录之外，已拒绝。",
                });

                continue;
            }

            if (!ArchiveService.TryGetSafeTarget(gameDir, relative, out var target, out var reason))
            {
                steps.Add(new CopyStep
                {
                    SourcePath = file,
                    RelativeTarget = relative,
                    BlockReason = $"目标路径不安全：{reason}",
                });

                continue;
            }

            long size = 0;

            try
            {
                size = new FileInfo(file).Length;
            }
            catch
            {
                // 大小读不到不影响计划，仅用于展示
            }

            steps.Add(new CopyStep
            {
                SourcePath = file,
                TargetPath = target,
                RelativeTarget = relative,
                SourceSize = size,
                TargetExists = File.Exists(target),
            });
        }

        if (steps.Count == 0)
        {
            steps.Add(new CopyStep { BlockReason = "解压目录中没有可复制的文件。" });
        }

        return steps;
    }

    /// <summary>
    /// 压缩包条目预览（**尚未解压**）。
    /// 条目本身还不是文件，所以 SourcePath 为空，仅供用户确认目录结构。
    /// 真正安装前必须先解压，再调用 <see cref="PlanFromExtractDirectory"/>。
    /// </summary>
    public List<CopyStep> PreviewArchiveEntries(
        ArchiveInfo archive,
        string gameDir,
        bool keepOriginalStructure = false)
    {
        var steps = new List<CopyStep>();

        var prefix = keepOriginalStructure ? null : archive.RootPrefix;

        foreach (var entry in archive.Entries.Where(e => !e.IsDirectory))
        {
            var relative = string.IsNullOrWhiteSpace(entry.RelativePath)
                ? entry.FullPath
                : entry.RelativePath;

            if (!ArchiveService.TryGetSafeTarget(gameDir, relative, out var target, out var reason))
            {
                steps.Add(new CopyStep
                {
                    RelativeTarget = relative,
                    BlockReason = $"目标路径不安全：{reason}",
                });

                continue;
            }

            steps.Add(new CopyStep
            {
                TargetPath = target,
                RelativeTarget = relative,
                SourceSize = entry.Size,
                TargetExists = File.Exists(target),
            });
        }

        return steps;
    }

    // ------------------------------------------------------------------ 安装

    /// <summary>
    /// 执行安装。
    /// </summary>
    /// <param name="plan">由 PlanFromExtractDirectory 生成的计划。</param>
    /// <param name="game">目标游戏。</param>
    /// <param name="patchKey">补丁去重键。</param>
    public async Task<InstallReport> InstallAsync(
        IReadOnlyList<CopyStep> plan,
        GameEntry game,
        string patchKey,
        CancellationToken cancellationToken = default)
    {
        var report = new InstallReport
        {
            GameId = game.Id,
            GameName = game.Name,
            PatchKey = patchKey,
            PatchPath = game.InstallDir,
        };

        if (plan.Count == 0)
        {
            report.Error = "安装计划为空。";
            return report;
        }

        var blocked = plan.FirstOrDefault(s => s.IsBlocked);

        if (blocked is not null)
        {
            report.Error = $"安装计划被拒绝：{blocked.BlockReason}";
            Log.Error(report.Error);
            return report;
        }

        if (!Directory.Exists(game.InstallDir))
        {
            report.Error = $"游戏目录不存在: {game.InstallDir}";
            Log.Error(report.Error);
            return report;
        }

        // 备份批次目录
        var batchId = $"{DateTime.Now:yyyyMMdd-HHmmss}-{Sanitize(patchKey)}";
        report.BackupBatchId = batchId;

        // 回滚账本：记录本次新增的文件，失败时删除
        var createdFiles = new List<string>();

        try
        {
            foreach (var step in plan)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (!File.Exists(step.SourcePath))
                {
                    report.Error = $"找不到补丁源文件: {step.SourcePath}";
                    Log.Error(report.Error);
                    break;
                }

                var targetIsNew = !File.Exists(step.TargetPath);

                if (!targetIsNew && _settings.BackupBeforeOverwrite)
                {
                    var backup = await BackupFileAsync(
                            step.TargetPath, game.Id, patchKey, batchId, cancellationToken)
                        .ConfigureAwait(false);

                    if (backup is null)
                    {
                        report.Error = $"备份失败，已中止安装: {step.TargetPath}";
                        Log.Error(report.Error);
                        break;
                    }

                    report.Backups.Add(backup);
                    report.FilesBackedUp++;
                }

                var tempTarget = step.TargetPath + ".galpatch-new";

                try
                {
                    var targetDir = Path.GetDirectoryName(step.TargetPath);

                    if (!string.IsNullOrEmpty(targetDir)) Directory.CreateDirectory(targetDir);

                    await using (var input = new FileStream(
                        step.SourcePath, FileMode.Open, FileAccess.Read, FileShare.Read,
                        1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
                    await using (var output = new FileStream(
                        tempTarget, FileMode.Create, FileAccess.Write, FileShare.None,
                        1024 * 1024, FileOptions.Asynchronous))
                    {
                        await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
                    }

                    if (targetIsNew) createdFiles.Add(step.TargetPath);

                    File.Move(tempTarget, step.TargetPath, overwrite: true);

                    var written = new FileInfo(step.TargetPath).Length;

                    report.FilesCopied++;
                    report.BytesCopied += written;
                    report.Steps.Add($"{step.ActionText}: {step.RelativeTarget}（{written:N0} 字节）");

                    Log.Info($"已写入补丁文件: {step.TargetPath}");
                }
                catch (Exception ex)
                {
                    report.Error = $"复制失败: {step.TargetPath} -> {ex.Message}";

                    TryDelete(tempTarget);

                    Log.Error(report.Error, ex);
                    break;
                }
            }

            if (report.Error.Length > 0)
            {
                if (_settings.AutoRollbackOnFailure)
                {
                    await RollbackAsync(report, createdFiles).ConfigureAwait(false);
                }

                return report;
            }

            report.Success = report.FilesCopied > 0;

            if (!report.Success)
            {
                report.Error = "没有任何文件被复制。";
            }

            return report;
        }
        catch (OperationCanceledException)
        {
            report.Error = "安装已取消。";

            if (_settings.AutoRollbackOnFailure)
            {
                await RollbackAsync(report, createdFiles).ConfigureAwait(false);
            }

            throw;
        }
        catch (Exception ex)
        {
            report.Error = $"安装过程中发生未预期的错误: {ex.Message}";
            Log.Error("安装失败", ex);

            if (_settings.AutoRollbackOnFailure)
            {
                await RollbackAsync(report, createdFiles).ConfigureAwait(false);
            }

            return report;
        }
    }

    // ------------------------------------------------------------------ 备份

    /// <summary>备份单个文件。</summary>
    public async Task<BackupEntry?> BackupFileAsync(
        string filePath,
        string gameId,
        string patchKey,
        string batchId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (!File.Exists(filePath)) return null;

            var info = new FileInfo(filePath);

            var key = Path.Combine(batchId, Sanitize(gameId), Guid.NewGuid().ToString("N")[..8])
                      + "_" + Sanitize(Path.GetFileName(filePath));

            var backupPath = Path.Combine(Paths.BackupFolder, key);

            var dir = Path.GetDirectoryName(backupPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            await using (var input = new FileStream(
                filePath, FileMode.Open, FileAccess.Read, FileShare.Read,
                1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
            await using (var output = new FileStream(
                backupPath, FileMode.Create, FileAccess.Write, FileShare.None,
                1024 * 1024, FileOptions.Asynchronous))
            {
                await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
            }

            var entry = new BackupEntry
            {
                OriginalPath = filePath,
                BackupPath = backupPath,
                GameId = gameId,
                PatchKey = patchKey,
                Size = info.Length,
                Sha256 = await FileHash.Sha256Async(backupPath, cancellationToken).ConfigureAwait(false),
                CreatedAt = DateTime.Now,
            };

            Log.Info($"已备份: {filePath} -> {backupPath}");
            return entry;
        }
        catch (Exception ex)
        {
            Log.Error($"备份失败: {filePath}", ex);
            return null;
        }
    }

    /// <summary>按备份记录还原文件。</summary>
    public int RestoreBackups(IEnumerable<BackupEntry> entries)
    {
        var restored = 0;

        foreach (var entry in entries)
        {
            try
            {
                if (!File.Exists(entry.BackupPath))
                {
                    Log.Warn($"备份文件已不存在，跳过: {entry.BackupPath}");
                    continue;
                }

                var dir = Path.GetDirectoryName(entry.OriginalPath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

                File.Copy(entry.BackupPath, entry.OriginalPath, overwrite: true);
                restored++;

                Log.Info($"已还原: {entry.OriginalPath}");
            }
            catch (Exception ex)
            {
                Log.Error($"还原失败: {entry.OriginalPath}", ex);
            }
        }

        return restored;
    }

    /// <summary>安装失败后的回滚：删除新增文件 + 还原被覆盖文件。</summary>
    private Task RollbackAsync(InstallReport report, List<string> createdFiles)
    {
        Log.Warn($"开始回滚（批次 {report.BackupBatchId}）...");

        // 1) 删除本次新增的文件
        foreach (var created in createdFiles)
        {
            TryDelete(created);
        }

        // 2) 还原被覆盖的文件
        if (report.Backups.Count > 0)
        {
            RestoreBackups(report.Backups);
        }

        report.RolledBack = true;
        report.Success = false;

        Log.Warn($"回滚完成：删除新增 {createdFiles.Count} 个，还原 {report.Backups.Count} 个。");

        return Task.CompletedTask;
    }

    /// <summary>清理超过保留期的备份。</summary>
    public void CleanupOldBackups()
    {
        try
        {
            if (!Directory.Exists(Paths.BackupFolder)) return;

            var cutoff = DateTime.Now.AddDays(-_settings.BackupRetentionDays);

            foreach (var dir in Directory.GetDirectories(Paths.BackupFolder))
            {
                var info = new DirectoryInfo(dir);

                if (info.LastWriteTime < cutoff)
                {
                    Directory.Delete(dir, recursive: true);
                    Log.Info($"已清理过期备份: {dir}");
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"清理备份失败: {ex.Message}");
        }
    }

    // ------------------------------------------------------------------ 工具

    /// <summary>补丁去重键：文件名 + 大小的短哈希（不读全文件，避免卡界面）。</summary>
    public static string BuildPatchKey(string filePath)
    {
        try
        {
            var info = new FileInfo(filePath);
            var raw = $"{info.Name.ToLowerInvariant()}|{info.Length}";
            var hash = System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(raw));

            return Convert.ToHexString(hash)[..16].ToLowerInvariant();
        }
        catch
        {
            return Guid.NewGuid().ToString("N")[..16];
        }
    }

    private static string Sanitize(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "unknown";

        var invalid = Path.GetInvalidFileNameChars();
        var chars = value.Select(c => invalid.Contains(c) ? '_' : c).ToArray();
        var result = new string(chars).Trim();

        return result.Length > 60 ? result[..60] : result;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex)
        {
            Log.Warn($"删除失败: {path} -> {ex.Message}");
        }
    }
}

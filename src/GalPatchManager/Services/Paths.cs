namespace GalPatchManager.Services;

/// <summary>
/// 统一管理配置、日志、备份等落盘位置。
/// 全部位于用户 AppData，符合“配置与日志保存在用户 AppData 目录”的要求。
/// </summary>
public static class Paths
{
    /// <summary>%AppData%\GalPatchManager</summary>
    public static string AppDataRoot { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "GalPatchManager");

    public static string ConfigFile => Path.Combine(AppDataRoot, "config.json");

    public static string HistoryFile => Path.Combine(AppDataRoot, "history.json");

    public static string DownloadsStateFile => Path.Combine(AppDataRoot, "downloads.json");

    /// <summary>日志目录：%AppData%\GalPatchManager\Logs</summary>
    public static string LogFolder => Path.Combine(AppDataRoot, "Logs");

    /// <summary>备份目录：%AppData%\GalPatchManager\Backups</summary>
    public static string BackupFolder => Path.Combine(AppDataRoot, "Backups");

    /// <summary>临时解压目录：%AppData%\GalPatchManager\Temp</summary>
    public static string TempFolder => Path.Combine(AppDataRoot, "Temp");

    /// <summary>下载失败/拒收文件的隔离目录。</summary>
    public static string QuarantineFolder => Path.Combine(AppDataRoot, "Quarantine");

    public static string TodayLogFile =>
        Path.Combine(LogFolder, $"galpatch-{DateTime.Now:yyyyMMdd}.log");

    /// <summary>创建所有必需目录（幂等）。</summary>
    public static void EnsureCreated()
    {
        Directory.CreateDirectory(AppDataRoot);
        Directory.CreateDirectory(LogFolder);
        Directory.CreateDirectory(BackupFolder);
        Directory.CreateDirectory(TempFolder);
        Directory.CreateDirectory(QuarantineFolder);
    }

    /// <summary>
    /// 清理旧的临时解压目录。只删除本程序自己创建的目录，不会碰用户的其他文件。
    /// </summary>
    public static void CleanTempQuietly(TimeSpan olderThan)
    {
        try
        {
            if (!Directory.Exists(TempFolder)) return;

            foreach (var dir in Directory.GetDirectories(TempFolder))
            {
                try
                {
                    var info = new DirectoryInfo(dir);
                    if (DateTime.Now - info.LastWriteTimeUtc.ToLocalTime() > olderThan)
                    {
                        Directory.Delete(dir, recursive: true);
                    }
                }
                catch (Exception ex)
                {
                    Log.Warn($"清理临时目录失败: {dir} -> {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"清理临时目录失败: {ex.Message}");
        }
    }
}

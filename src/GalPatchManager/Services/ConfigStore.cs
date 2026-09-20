using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using GalPatchManager.Models;

namespace GalPatchManager.Services;

/// <summary>
/// 配置 / 历史 的读写。写盘采用“先写临时文件再替换”，避免中途崩溃损坏配置。
/// </summary>
public static class ConfigStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        // 中文直接写入，不转成 \uXXXX，便于用户直接编辑排查
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    private static readonly object Gate = new();

    public static AppSettings LoadSettings()
    {
        Paths.EnsureCreated();

        var settings = ReadJson<AppSettings>(Paths.ConfigFile) ?? new AppSettings();
        settings.ApplyDefaults(Paths.AppDataRoot);
        return settings;
    }

    public static void SaveSettings(AppSettings settings)
    {
        settings.ApplyDefaults(Paths.AppDataRoot);
        WriteJson(Paths.ConfigFile, settings);
    }

    public static List<InstallRecord> LoadHistory()
    {
        Paths.EnsureCreated();
        return ReadJson<List<InstallRecord>>(Paths.HistoryFile) ?? new List<InstallRecord>();
    }

    public static void SaveHistory(IEnumerable<InstallRecord> records)
    {
        WriteJson(Paths.HistoryFile, records.ToList());
    }

    public static List<PatchDownloadItem> LoadDownloads()
    {
        Paths.EnsureCreated();
        return ReadJson<List<PatchDownloadItem>>(Paths.DownloadsStateFile) ?? new List<PatchDownloadItem>();
    }

    public static void SaveDownloads(IEnumerable<PatchDownloadItem> items)
    {
        WriteJson(Paths.DownloadsStateFile, items.ToList());
    }

    private static T? ReadJson<T>(string path) where T : class
    {
        try
        {
            if (!File.Exists(path)) return null;
            var text = File.ReadAllText(path, Encoding.UTF8);
            if (string.IsNullOrWhiteSpace(text)) return null;
            return JsonSerializer.Deserialize<T>(text, Options);
        }
        catch (Exception ex)
        {
            Log.Error($"读取失败，将使用默认值: {path}", ex);
            TryBackupBrokenFile(path);
            return null;
        }
    }

    private static void WriteJson<T>(string path, T value)
    {
        lock (Gate)
        {
            try
            {
                Paths.EnsureCreated();
                var json = JsonSerializer.Serialize(value, Options);
                var tmp = path + ".tmp";
                File.WriteAllText(tmp, json, new UTF8Encoding(false));

                if (File.Exists(path))
                {
                    // File.Replace 保留原文件的 ACL 并原子替换
                    File.Replace(tmp, path, null);
                }
                else
                {
                    File.Move(tmp, path);
                }
            }
            catch (Exception ex)
            {
                Log.Error($"写入失败: {path}", ex);
            }
        }
    }

    private static void TryBackupBrokenFile(string path)
    {
        try
        {
            if (!File.Exists(path)) return;
            var broken = path + $".broken-{DateTime.Now:yyyyMMddHHmmss}";
            File.Copy(path, broken, overwrite: true);
            Log.Warn($"已备份无法解析的文件: {broken}");
        }
        catch (Exception ex)
        {
            Log.Warn($"备份损坏文件失败: {ex.Message}");
        }
    }
}

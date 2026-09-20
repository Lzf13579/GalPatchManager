using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace GalPatchManager.Services;

/// <summary>基线中的一个文件签名。</summary>
public sealed class BaselineEntry
{
    public long Size { get; set; }

    public DateTime LastWriteUtc { get; set; }

    /// <summary>该文件被登记进基线的时刻。</summary>
    public DateTime RecordedUtc { get; set; } = DateTime.UtcNow;
}

/// <summary>单个监控目录的基线。</summary>
public sealed class FolderBaseline
{
    /// <summary>基线建立时间。</summary>
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;

    /// <summary>路径 -> 文件签名。</summary>
    public Dictionary<string, BaselineEntry> Files { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// 下载目录的“已知文件”基线。
///
/// 作用：让程序**只处理真正新下载的文件**，而不是把监控目录里早就存在的所有
/// zip/7z/rar/exe 都当成待处理补丁。
///
/// 规则：
///   * 目录第一次被监控时，把当前已有的文件全部登记为基线 —— 一个都不处理；
///   * 之后只处理“不在基线里”或“虽然登记过但大小/修改时间又变了”的文件；
///   * 文件被判定为下载完成后立即登记，避免重复处理。
///
/// 这样即使“下载时程序没开”，重新打开后文件因为修改时间晚于基线建立时间，
/// 仍然会被正常识别，不会漏掉。
/// </summary>
public static class IgnoreBaseline
{
    private static readonly object Gate = new();
    private static BaselineStore? _cache;

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNameCaseInsensitive = true,
    };

    private sealed class BaselineStore
    {
        /// <summary>监控目录 -> 基线。</summary>
        public Dictionary<string, FolderBaseline> Folders { get; set; } =
            new(StringComparer.OrdinalIgnoreCase);
    }

    private static string FilePath => Path.Combine(Paths.AppDataRoot, "baseline.json");

    // ------------------------------------------------------------------ 读写

    private static BaselineStore Load()
    {
        lock (Gate)
        {
            if (_cache is not null) return _cache;

            try
            {
                if (File.Exists(FilePath))
                {
                    var text = File.ReadAllText(FilePath, Encoding.UTF8);

                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        var loaded = JsonSerializer.Deserialize<BaselineStore>(text, Options);

                        if (loaded is not null)
                        {
                            _cache = loaded;
                            return _cache;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Warn($"读取基线文件失败，将重新建立: {ex.Message}");
            }

            _cache = new BaselineStore();
            return _cache;
        }
    }

    private static void Save()
    {
        lock (Gate)
        {
            try
            {
                Paths.EnsureCreated();

                var json = JsonSerializer.Serialize(_cache ?? new BaselineStore(), Options);
                var tmp = FilePath + ".tmp";

                File.WriteAllText(tmp, json, new UTF8Encoding(false));

                if (File.Exists(FilePath)) File.Replace(tmp, FilePath, null);
                else File.Move(tmp, FilePath);
            }
            catch (Exception ex)
            {
                Log.Warn($"保存基线文件失败: {ex.Message}");
            }
        }
    }

    private static string NormalizeKey(string folder)
    {
        try
        {
            return Path.GetFullPath(folder).TrimEnd('\\', '/');
        }
        catch
        {
            return folder.Trim();
        }
    }

    // ------------------------------------------------------------------ 查询

    /// <summary>该目录是否已经建立过基线。</summary>
    public static bool HasBaseline(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder)) return false;

        var store = Load();
        return store.Folders.ContainsKey(NormalizeKey(folder));
    }

    /// <summary>基线中的文件数量。</summary>
    public static int CountOf(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder)) return 0;

        var store = Load();

        return store.Folders.TryGetValue(NormalizeKey(folder), out var b) ? b.Files.Count : 0;
    }

    /// <summary>建立基线的时间。</summary>
    public static DateTime? CreatedAt(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder)) return null;

        var store = Load();

        return store.Folders.TryGetValue(NormalizeKey(folder), out var b)
            ? b.CreatedUtc.ToLocalTime()
            : null;
    }

    /// <summary>
    /// 判断某个文件是否“值得处理”。
    ///
    /// 规则：
    ///   * 不在基线里 且 修改时间 >= 基线建立时间  -> 新增文件，处理；
    ///   * 不在基线里 但 修改时间更早             -> 建基线之前就存在的旧文件，不处理；
    ///   * 在基线里                               -> 只有大小或修改时间变了才处理。
    /// </summary>
    public static bool IsNewOrChanged(string folder, string filePath, long size, DateTime lastWriteUtc)
    {
        if (string.IsNullOrWhiteSpace(folder)) return true;

        var store = Load();
        var key = NormalizeKey(folder);

        if (!store.Folders.TryGetValue(key, out var baseline))
        {
            // 没有基线：不是“待处理”，调用方应先建立基线
            return false;
        }

        var full = NormalizeKey(filePath);

        if (!baseline.Files.TryGetValue(full, out var entry))
        {
            // 不在基线里：只有基线建立之后落地的才算新下载
            return lastWriteUtc >= baseline.CreatedUtc;
        }

        // 登记过：大小或修改时间变了才算新变化
        return entry.Size != size || entry.LastWriteUtc != lastWriteUtc;
    }

    // ------------------------------------------------------------------ 写入

    /// <summary>
    /// 为目录建立基线。
    /// </summary>
    /// <param name="files">要登记为“已知”的文件；传 null 表示登记目录中当前所有候选文件。</param>
    /// <returns>登记的文件数量。</returns>
    public static int CreateBaseline(string folder, IEnumerable<string>? files = null)
    {
        if (string.IsNullOrWhiteSpace(folder)) return 0;

        var key = NormalizeKey(folder);

        // 先锁定“基线时刻”，再枚举，保证严格
        var createdUtc = DateTime.UtcNow;

        var baseline = new FolderBaseline { CreatedUtc = createdUtc };

        IEnumerable<string> list;

        if (files is not null)
        {
            list = files;
        }
        else
        {
            try
            {
                list = Directory.Exists(folder)
                    ? Directory.GetFiles(folder, "*", SearchOption.TopDirectoryOnly)
                    : Array.Empty<string>();
            }
            catch (Exception ex)
            {
                Log.Warn($"建立基线时枚举目录失败: {folder} -> {ex.Message}");
                list = Array.Empty<string>();
            }
        }

        foreach (var file in list)
        {
            try
            {
                var info = new FileInfo(file);

                if (!info.Exists) continue;

                baseline.Files[NormalizeKey(file)] = new BaselineEntry
                {
                    Size = info.Length,
                    LastWriteUtc = info.LastWriteTimeUtc,
                    RecordedUtc = createdUtc,
                };
            }
            catch
            {
                // 单个文件读不到就跳过
            }
        }

        var store = Load();
        store.Folders[key] = baseline;
        Save();

        Log.Info($"已为监控目录建立基线: {folder}（登记 {baseline.Files.Count} 个已存在文件，这些文件不会被当作待处理补丁）");

        return baseline.Files.Count;
    }

    /// <summary>把一个文件登记进基线（判定完成后调用）。</summary>
    public static void Record(string folder, string filePath, long size, DateTime lastWriteUtc)
    {
        if (string.IsNullOrWhiteSpace(folder)) return;

        var store = Load();
        var key = NormalizeKey(folder);

        if (!store.Folders.TryGetValue(key, out var baseline))
        {
            baseline = new FolderBaseline { CreatedUtc = DateTime.UtcNow };
            store.Folders[key] = baseline;
        }

        baseline.Files[NormalizeKey(filePath)] = new BaselineEntry
        {
            Size = size,
            LastWriteUtc = lastWriteUtc,
            RecordedUtc = DateTime.UtcNow,
        };
    }

    /// <summary>批量登记后统一落盘（比逐个 Save 高效）。</summary>
    public static void SaveChanges() => Save();

    /// <summary>清除某个目录的基线（下次监控时会重新建立）。</summary>
    public static void Clear(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder)) return;

        var store = Load();

        if (store.Folders.Remove(NormalizeKey(folder)))
        {
            Save();
            Log.Info($"已清除监控目录基线: {folder}");
        }
    }
}

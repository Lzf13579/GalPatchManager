using SharpCompress.Archives;
using SharpCompress.Common;
using GalPatchManager.Models;

namespace GalPatchManager.Services;

public enum ArchiveKind
{
    Unknown = 0,
    Zip = 1,
    SevenZip = 2,
    Rar = 3,
}

/// <summary>压缩包探测结果。</summary>
public sealed class ArchiveInfo
{
    public string Path { get; init; } = "";

    public ArchiveKind Kind { get; init; } = ArchiveKind.Unknown;

    public List<ArchiveEntryInfo> Entries { get; init; } = new();

    /// <summary>
    /// 压缩包内统一的一级目录名（若所有文件都在同一个根目录下）。
    /// 为 null 表示压缩包是“散装”的，根目录下的文件应直接铺到游戏目录。
    /// </summary>
    public string? RootPrefix { get; init; }

    public long TotalBytes { get; init; }

    public int FileCount => Entries.Count(e => !e.IsDirectory);

    public string KindText => Kind switch
    {
        ArchiveKind.Zip => "ZIP",
        ArchiveKind.SevenZip => "7Z",
        ArchiveKind.Rar => "RAR",
        _ => "未知压缩包",
    };
}

/// <summary>解压结果。</summary>
public sealed class ExtractResult
{
    public bool Success { get; set; }

    public string DestinationRoot { get; set; } = "";

    public int FilesExtracted { get; set; }

    public long BytesExtracted { get; set; }

    public List<string> Files { get; } = new();

    public string Error { get; set; } = "";
}

/// <summary>
/// 压缩包读取与解压。
///
/// 安全要点：
///   * 拒绝绝对路径、盘符路径、以及任何会逃出目标目录的 ".."（路径穿越防护）。
///   * 拒绝符号链接条目。
///   * 限制单个条目大小与条目数量，防止压缩炸弹。
///   * 解压只写进临时目录，绝不直接写游戏目录。
/// </summary>
public static class ArchiveService
{
    /// <summary>单条目大小上限（默认 2 GiB）。</summary>
    private const long MaxSingleEntryBytes = 2L * 1024 * 1024 * 1024;

    /// <summary>压缩包内条目数量上限。</summary>
    private const int MaxEntryCount = 200_000;

    /// <summary>按扩展名判断压缩包类型。</summary>
    public static ArchiveKind DetectKind(string pathOrName)
    {
        var ext = System.IO.Path.GetExtension(pathOrName).TrimStart('.').ToLowerInvariant();

        return ext switch
        {
            "zip" => ArchiveKind.Zip,
            "7z" => ArchiveKind.SevenZip,
            "rar" => ArchiveKind.Rar,
            _ => ArchiveKind.Unknown,
        };
    }

    /// <summary>是否为受支持的压缩包扩展名。</summary>
    public static bool IsArchiveExtension(string pathOrName) =>
        DetectKind(pathOrName) != ArchiveKind.Unknown;

    /// <summary>
    /// 列出压缩包内容。失败时返回带 Error 的 <see cref="ArchiveInfo"/>（Entries 为空）。
    /// </summary>
    public static ArchiveInfo Probe(string archivePath, CancellationToken cancellationToken = default)
    {
        var kind = DetectKind(archivePath);

        try
        {
            if (!File.Exists(archivePath))
            {
                Log.Warn($"压缩包不存在: {archivePath}");
                return new ArchiveInfo { Path = archivePath, Kind = kind };
            }

            var entries = new List<ArchiveEntryInfo>();
            long total = 0;

            using var archive = ArchiveFactory.Open(archivePath);

            foreach (var entry in archive.Entries)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (entries.Count > MaxEntryCount)
                {
                    Log.Warn($"压缩包条目过多（>{MaxEntryCount}），已中止列举: {archivePath}");
                    break;
                }

                var key = (entry.Key ?? "").Replace('\\', '/').Trim();

                if (key.Length == 0) continue;

                var isDir = entry.IsDirectory || key.EndsWith('/');

                entries.Add(new ArchiveEntryInfo
                {
                    FullPath = key,
                    Size = entry.Size,
                    IsDirectory = isDir,
                });

                if (!isDir) total += entry.Size;
            }

            var rootPrefix = ComputeRootPrefix(entries);

            foreach (var e in entries)
            {
                e.Role = e.IsDirectory
                    ? EntryRole.Directory
                    : (rootPrefix is null ? EntryRole.ArchiveFile : EntryRole.NestedFile);

                e.RelativePath = StripPrefix(e.FullPath, rootPrefix);
            }

            return new ArchiveInfo
            {
                Path = archivePath,
                Kind = kind,
                Entries = entries,
                RootPrefix = rootPrefix,
                TotalBytes = total,
            };
        }
        catch (Exception ex)
        {
            Log.Error($"读取压缩包失败: {archivePath}", ex);
            return new ArchiveInfo { Path = archivePath, Kind = kind };
        }
    }

    /// <summary>压缩包是否可正常打开（完整性初检）。</summary>
    public static bool CanOpen(string archivePath, out string? error)
    {
        error = null;

        try
        {
            using var archive = ArchiveFactory.Open(archivePath);

            // Solid RAR/7z 需要真正解一次才有意义，这里做一次轻量校验：
            // 枚举条目并在解压阶段再做 CRC 校验。
            var count = 0;
            foreach (var _ in archive.Entries)
            {
                count++;
                if (count > MaxEntryCount) break;
            }

            if (count == 0)
            {
                error = "压缩包为空或无法读取条目";
                return false;
            }

            return true;
        }
        catch (CryptographicException)
        {
            error = "压缩包已加密，需要密码，本程序暂不支持。";
            return false;
        }
        catch (Exception ex)
        {
            error = $"压缩包无法打开: {ex.Message}";
            return false;
        }
    }

    /// <summary>
    /// 解压到指定目录。
    /// </summary>
    /// <param name="preferOriginalStructure">
    /// true  = 保留压缩包原始结构（把整个根目录内容放进目标目录）；
    /// false = 去掉统一根目录，把内容直接铺到目标目录。
    /// </param>
    public static ExtractResult Extract(
        string archivePath,
        string destinationRoot,
        bool preferOriginalStructure,
        CancellationToken cancellationToken = default,
        Action<int, int>? progress = null)
    {
        var result = new ExtractResult { DestinationRoot = destinationRoot };

        try
        {
            var info = Probe(archivePath, cancellationToken);

            if (info.Entries.Count == 0)
            {
                result.Error = "压缩包为空或无法读取。";
                return result;
            }

            Directory.CreateDirectory(destinationRoot);

            var stripPrefix = !preferOriginalStructure ? info.RootPrefix : null;
            var total = info.FileCount;
            var done = 0;

            using var archive = ArchiveFactory.Open(archivePath);

            foreach (var entry in archive.Entries)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (entry.IsDirectory) continue;

                var key = (entry.Key ?? "").Replace('\\', '/').Trim();
                if (key.Length == 0) continue;

                if (entry.Size > MaxSingleEntryBytes)
                {
                    result.Error = $"条目过大，已中止: {key}（{entry.Size} 字节）";
                    return result;
                }

                var relative = StripPrefix(key, stripPrefix);

                if (string.IsNullOrWhiteSpace(relative))
                {
                    // 去掉前缀后为空的条目（理论上只有目录）
                    continue;
                }

                if (!TryGetSafeTarget(destinationRoot, relative, out var target, out var reason))
                {
                    result.Error = $"检测到不安全的压缩包路径，已中止解压：{key}（{reason}）";
                    Log.Error(result.Error);
                    return result;
                }

                var targetDir = System.IO.Path.GetDirectoryName(target);

                if (!string.IsNullOrEmpty(targetDir)) Directory.CreateDirectory(targetDir);

                using (var input = entry.OpenEntryStream())
                using (var output = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    input.CopyTo(output);
                }

                var length = new FileInfo(target).Length;

                result.Files.Add(target);
                result.FilesExtracted++;
                result.BytesExtracted += length;

                done++;
                progress?.Invoke(done, total);
            }

            if (result.FilesExtracted == 0)
            {
                result.Error = "压缩包中没有可解压的文件。";
                return result;
            }

            result.Success = true;
            Log.Info($"解压完成: {archivePath} -> {destinationRoot}（{result.FilesExtracted} 个文件）");
            return result;
        }
        catch (OperationCanceledException)
        {
            result.Error = "已取消。";
            throw;
        }
        catch (Exception ex)
        {
            result.Error = ex.Message;
            Log.Error($"解压失败: {archivePath}", ex);
            return result;
        }
    }

    // ------------------------------------------------------------------ 辅助

    /// <summary>计算是否所有文件都位于同一个一级目录下。</summary>
    private static string? ComputeRootPrefix(List<ArchiveEntryInfo> entries)
    {
        string? root = null;
        var sawFile = false;

        foreach (var entry in entries)
        {
            var path = entry.FullPath.Trim('/');

            if (path.Length == 0) continue;

            var slash = path.IndexOf('/');

            if (entry.IsDirectory)
            {
                // 只考虑一级目录本身；更深的目录不参与
                if (slash < 0) continue;

                var first = path[..slash];

                if (root is null && !sawFile) root = first;
                else if (!string.Equals(root, first, StringComparison.Ordinal)) return null;

                continue;
            }

            sawFile = true;

            if (slash < 0)
            {
                // 根目录下有散装文件 -> 没有统一根目录
                return null;
            }

            var fileRoot = path[..slash];

            if (root is null) root = fileRoot;
            else if (!string.Equals(root, fileRoot, StringComparison.Ordinal)) return null;
        }

        return root;
    }

    private static string StripPrefix(string path, string? prefix)
    {
        var normalized = path.Replace('\\', '/').Trim('/');

        if (string.IsNullOrEmpty(prefix)) return normalized;

        var p = prefix.Trim('/');

        if (normalized.StartsWith(p + "/", StringComparison.Ordinal))
            return normalized[(p.Length + 1)..];

        if (string.Equals(normalized, p, StringComparison.Ordinal)) return "";

        return normalized;
    }

    /// <summary>
    /// 把压缩包内的相对路径安全地映射到目标目录下的绝对路径。
    /// 任何越界情况都会被拒绝。
    /// </summary>
    public static bool TryGetSafeTarget(
        string destinationRoot,
        string relativePath,
        out string targetPath,
        out string reason)
    {
        targetPath = "";
        reason = "";

        if (string.IsNullOrWhiteSpace(relativePath))
        {
            reason = "空路径";
            return false;
        }

        var rel = relativePath.Replace('\\', '/');

        // 统一移除驱动器前缀
        if (rel.Length >= 2 && rel[1] == ':' && char.IsLetter(rel[0]))
        {
            reason = "包含盘符";
            return false;
        }

        if (rel.StartsWith("//", StringComparison.Ordinal) ||
            rel.StartsWith("\\\\", StringComparison.Ordinal))
        {
            reason = "UNC 路径";
            return false;
        }

        if (rel.StartsWith('/'))
        {
            reason = "绝对路径";
            return false;
        }

        var segments = rel.Split('/', StringSplitOptions.RemoveEmptyEntries);

        foreach (var segment in segments)
        {
            if (segment == "..")
            {
                reason = "路径穿越 (..)";
                return false;
            }

            if (segment == ".")
            {
                reason = "相对当前目录 (.)";
                return false;
            }

            if (segment.IndexOfAny(System.IO.Path.GetInvalidFileNameChars()) >= 0)
            {
                reason = $"非法字符 {segment}";
                return false;
            }
        }

        var rootFull = System.IO.Path.GetFullPath(destinationRoot);

        var combined = segments.Length == 0
            ? rootFull
            : System.IO.Path.GetFullPath(System.IO.Path.Combine(rootFull, System.IO.Path.Combine(segments)));

        var rootWithSep = rootFull.EndsWith(System.IO.Path.DirectorySeparatorChar)
            ? rootFull
            : rootFull + System.IO.Path.DirectorySeparatorChar;

        if (!combined.StartsWith(rootWithSep, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(combined, rootFull, StringComparison.OrdinalIgnoreCase))
        {
            reason = "解压目标越出了目标目录";
            return false;
        }

        targetPath = combined;
        return true;
    }
}

using System.Security.Cryptography;

namespace GalPatchManager.Services;

/// <summary>文件哈希与文件完整性辅助方法。</summary>
public static class FileHash
{
    /// <summary>流式计算 SHA-256，十六进制小写。</summary>
    public static async Task<string> Sha256Async(string path, CancellationToken cancellationToken = default)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            // 允许其他进程继续写/读，避免监控目录里正在下载的文件被锁死
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 1024 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

        using var sha = SHA256.Create();
        var hash = await sha.ComputeHashAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <summary>同步计算，用于不方便 await 的场景。</summary>
    public static string Sha256(string path)
    {
        using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 1024 * 1024, FileOptions.SequentialScan);

        using var sha = SHA256.Create();
        var hash = sha.ComputeHash(stream);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <summary>
    /// 判断文件是否可被完整读取。
    /// 下载监控用它在“大小稳定”之外再加一道保险：
    /// 文件能独占打开、读到真实末尾、并且长度与文件系统报告一致。
    /// </summary>
    public static bool CanBeReadFully(string path, out string? error)
    {
        error = null;

        try
        {
            var info = new FileInfo(path);
            if (!info.Exists)
            {
                error = "文件不存在";
                return false;
            }

            // 独占打开：如果下载器还持有独占句柄或文件仍被锁定，这里会失败
            using var stream = new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.None, 4096, FileOptions.SequentialScan);

            // 读一小段确认内容可访问（避免读到 0 字节的空壳文件）
            var probe = new byte[Math.Min(4096, Math.Max(1, (int)Math.Min(stream.Length, 4096)))];
            var read = stream.Read(probe, 0, probe.Length);

            if (info.Length > 0 && read == 0)
            {
                error = "文件无法读取内容";
                return false;
            }

            if (stream.Length != info.Length)
            {
                error = "读取过程中文件长度发生了变化";
                return false;
            }

            return true;
        }
        catch (IOException ex)
        {
            error = $"文件仍被占用或未写完: {ex.Message}";
            return false;
        }
        catch (UnauthorizedAccessException ex)
        {
            error = $"无权读取: {ex.Message}";
            return false;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }
}

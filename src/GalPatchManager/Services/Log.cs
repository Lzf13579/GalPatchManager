using System.Text;

namespace GalPatchManager.Services;

/// <summary>
/// 极简日志实现：写入 %AppData%\GalPatchManager\Logs\galpatch-yyyyMMdd.log，
/// 同时抛出一个事件给界面显示。
/// </summary>
public static class Log
{
    private static readonly object Gate = new();

    /// <summary>界面订阅此事件以显示实时日志。</summary>
    public static event Action<string>? MessageLogged;

    public static void Info(string message) => Write("INFO", message);

    public static void Warn(string message) => Write("WARN", message);

    public static void Error(string message) => Write("ERROR", message);

    public static void Error(string message, Exception ex) =>
        Write("ERROR", $"{message} :: {ex.GetType().Name}: {ex.Message}");

    public static void Debug(string message) => Write("DEBUG", message);

    private static void Write(string level, string message)
    {
        var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [{level}] {message}";

        try
        {
            lock (Gate)
            {
                Paths.EnsureCreated();
                File.AppendAllText(Paths.TodayLogFile, line + Environment.NewLine, Encoding.UTF8);
            }
        }
        catch
        {
            // 日志写盘失败不能影响主流程
        }

        try
        {
            MessageLogged?.Invoke(line);
        }
        catch
        {
            // 忽略订阅者异常
        }
    }
}

using System.Diagnostics;
using System.Text.RegularExpressions;
using GalPatchManager.Models;
using Microsoft.Win32;

namespace GalPatchManager.Services;

/// <summary>
/// Steam 游戏自动识别。
///
/// 识别链路（不局限默认库）：
///   1) 注册表 HKCU\Software\Valve\Steam\SteamPath / HKCU\...\Apps\steam.exe
///      以及 HKLM\SOFTWARE\WOW6432Node\Valve\Steam\InstallPath
///   2) 从 Steam 根目录读取 steamapps\libraryfolders.vdf，得到**所有**库路径
///      （同时兼容新版嵌套写法与老版扁平写法）
///   3) 合并用户手工补充的额外库路径、以及从其他已知 Steam 安装位置推断的库
///   4) 在每个库的 steamapps\ 下解析所有 appmanifest_*.acf
/// </summary>
public sealed class SteamDetector
{
    private static readonly Regex AppIdRegex =
        new(@"appmanifest_(\d+)\.acf$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Steam 根目录；未找到时为 null。</summary>
    public string? SteamRoot { get; private set; }

    /// <summary>本次扫描到的所有库路径。</summary>
    public List<string> LibraryPaths { get; } = new();

    /// <summary>扫描过程中的提示信息，供界面展示（不视为错误）。</summary>
    public List<string> Diagnostics { get; } = new();

    /// <summary>
    /// 执行完整扫描。
    /// </summary>
    /// <param name="extraLibraryPaths">用户配置里额外补充的库路径。</param>
    public async Task<List<GameEntry>> DetectAsync(
        IEnumerable<string>? extraLibraryPaths = null,
        CancellationToken cancellationToken = default)
    {
        LibraryPaths.Clear();
        Diagnostics.Clear();

        SteamRoot = FindSteamRoot();

        if (SteamRoot is null)
        {
            Diagnostics.Add("未在注册表或常见位置找到 Steam 安装。你仍然可以手动添加游戏目录。");
            Log.Warn("未找到 Steam 安装路径。");
        }
        else
        {
            Log.Info($"Steam 根目录: {SteamRoot}");
        }

        var libraries = new List<string>();

        if (SteamRoot is not null)
        {
            libraries.Add(Path.Combine(SteamRoot, "steamapps"));
            libraries.AddRange(ReadLibraryFolders(SteamRoot));
        }

        if (extraLibraryPaths is not null)
        {
            foreach (var extra in extraLibraryPaths)
            {
                if (!string.IsNullOrWhiteSpace(extra)) libraries.Add(extra);
            }
        }

        // 归一化 + 去重：库既可能被写成库根目录，也可能被写成 ...\steamapps
        var normalized = new List<string>();

        foreach (var lib in libraries)
        {
            foreach (var candidate in NormalizeToSteamApps(lib))
            {
                var full = SafeFullPath(candidate);
                if (full is null) continue;

                if (normalized.Any(x => string.Equals(x, full, StringComparison.OrdinalIgnoreCase)))
                    continue;

                normalized.Add(full);
            }
        }

        LibraryPaths.AddRange(normalized);

        if (normalized.Count == 0)
        {
            Diagnostics.Add("没有可用的 Steam 库路径。");
            return new List<GameEntry>();
        }

        // 解析每个库
        var games = new List<GameEntry>();

        foreach (var steamApps in normalized)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!Directory.Exists(steamApps))
            {
                Diagnostics.Add($"库路径不存在，已跳过: {steamApps}");
                continue;
            }

            try
            {
                var found = await Task.Run(
                    () => ParseLibrary(steamApps, cancellationToken), cancellationToken)
                    .ConfigureAwait(false);

                games.AddRange(found);

                if (found.Count == 0)
                    Diagnostics.Add($"库中没有已安装游戏: {steamApps}");
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Diagnostics.Add($"解析库失败: {steamApps} -> {ex.Message}");
                Log.Error($"解析 Steam 库失败: {steamApps}", ex);
            }
        }

        // 同一游戏可能出现在多个库（迁移残留），按 AppID 去重并保留目录存在的那一份
        var result = games
            .GroupBy(g => g.AppId ?? g.InstallDir, StringComparer.OrdinalIgnoreCase)
            .Select(grp => grp
                .OrderByDescending(g => Directory.Exists(g.InstallDir))
                .ThenBy(g => g.InstallDir.Length)
                .First())
            .OrderBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        Log.Info($"Steam 识别完成: 库 {LibraryPaths.Count} 个, 游戏 {result.Count} 个。");
        return result;
    }

    // ---------------------------------------------------------------- 根目录

    private string? FindSteamRoot()
    {
        var candidates = new List<string?>();

        candidates.Add(ReadRegistryString(Registry.CurrentUser, @"Software\Valve\Steam", "SteamPath"));
        candidates.Add(ReadRegistryString(Registry.CurrentUser, @"Software\Valve\Steam", "SteamExe"));
        candidates.Add(ReadRegistryString(
            Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath"));
        candidates.Add(ReadRegistryString(
            Registry.LocalMachine, @"SOFTWARE\Valve\Steam", "InstallPath"));

        // 常见默认位置
        candidates.Add(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam"));
        candidates.Add(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Steam"));

        foreach (var raw in candidates)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;

            var path = raw.Trim().Trim('"');

            // SteamExe 指向 steam.exe，需要退到目录
            if (path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                path = Path.GetDirectoryName(path) ?? path;
            }

            path = path.Replace('/', '\\');

            if (LooksLikeSteamRoot(path)) return path;
        }

        return null;
    }

    private static bool LooksLikeSteamRoot(string path)
    {
        try
        {
            if (!Directory.Exists(path)) return false;

            // 只要存在 steam.exe 或 steamapps 目录就认为是有效根
            return File.Exists(Path.Combine(path, "steam.exe"))
                || Directory.Exists(Path.Combine(path, "steamapps"));
        }
        catch
        {
            return false;
        }
    }

    private static string? ReadRegistryString(RegistryKey root, string subKey, string name)
    {
        try
        {
            using var key = root.OpenSubKey(subKey);
            return key?.GetValue(name) as string;
        }
        catch (Exception ex)
        {
            Log.Warn($"读取注册表失败 {subKey}\\{name}: {ex.Message}");
            return null;
        }
    }

    // ------------------------------------------------------------ libraryfolders

    private IEnumerable<string> ReadLibraryFolders(string steamRoot)
    {
        var vdfPath = Path.Combine(steamRoot, "steamapps", "libraryfolders.vdf");
        var results = new List<string>();

        if (!File.Exists(vdfPath))
        {
            // 老版本 Steam 可能只有 config\libraryfolders.vdf
            var alt = Path.Combine(steamRoot, "config", "libraryfolders.vdf");
            if (File.Exists(alt)) vdfPath = alt;
            else
            {
                Diagnostics.Add("未找到 libraryfolders.vdf，只能识别默认库。");
                return results;
            }
        }

        var root = HandleVdf.TryParseFile(vdfPath);

        if (root is null)
        {
            Diagnostics.Add($"libraryfolders.vdf 解析失败: {vdfPath}");
            return results;
        }

        var container = root.FindChild("libraryfolders") ?? root.FindChild("LibraryFolders") ?? root;

        foreach (var node in container.GetChildren())
        {
            // 新版: "0" { "path" "D:\\SteamLibrary" ... }
            var path = node.GetValue("path");

            if (!string.IsNullOrWhiteSpace(path))
            {
                results.Add(path);
                continue;
            }

            // 老版: "1" "D:\\SteamLibrary"  （键是数字，值是路径）
            if (node.Values.Count == 0 && node.Children.Count == 0) continue;

            var legacy = node.GetValue("Path") ?? node.GetValue("path");
            if (!string.IsNullOrWhiteSpace(legacy)) results.Add(legacy);
        }

        // 补齐老版扁平写法：container 的直接键值对，键为数字
        foreach (var kv in container.Values)
        {
            if (kv.Key.All(char.IsDigit) && !string.IsNullOrWhiteSpace(kv.Value))
            {
                results.Add(kv.Value);
            }
        }

        if (results.Count == 0)
        {
            Diagnostics.Add("libraryfolders.vdf 中没有解析到额外库路径。");
        }

        return results;
    }

    /// <summary>把库路径归一化成 ...\steamapps 形式。</summary>
    private static IEnumerable<string> NormalizeToSteamApps(string libraryPath)
    {
        var p = libraryPath.Trim().Trim('"').Replace('/', '\\');

        if (p.Length == 0) yield break;

        // 已经是 steamapps
        if (p.EndsWith("steamapps", StringComparison.OrdinalIgnoreCase))
        {
            yield return p;
            yield break;
        }

        yield return Path.Combine(p, "steamapps");

        // 有些用户会把库直接指到 ...\steamapps\common，这里也兜一下
        if (p.EndsWith(Path.Combine("steamapps", "common"), StringComparison.OrdinalIgnoreCase))
        {
            yield return p;
        }
    }

    private static string? SafeFullPath(string path)
    {
        try
        {
            return Path.GetFullPath(path);
        }
        catch
        {
            return null;
        }
    }

    // -------------------------------------------------------------- appmanifest

    private List<GameEntry> ParseLibrary(string steamApps, CancellationToken cancellationToken)
    {
        var games = new List<GameEntry>();
        var commonDir = Path.Combine(steamApps, "common");

        string[] manifests;

        try
        {
            manifests = Directory.GetFiles(steamApps, "appmanifest_*.acf", SearchOption.TopDirectoryOnly);
        }
        catch (Exception ex)
        {
            Diagnostics.Add($"枚举 acf 失败: {steamApps} -> {ex.Message}");
            return games;
        }

        foreach (var manifest in manifests)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var match = AppIdRegex.Match(manifest);
            var appId = match.Success ? match.Groups[1].Value : null;

            var node = HandleVdf.TryParseFile(manifest);

            if (node is null)
            {
                Diagnostics.Add($"跳过无法解析的清单: {Path.GetFileName(manifest)}");
                continue;
            }

            // 有的版本把内容包在 "AppState" 下，有的直接是顶层
            var state = node.FindChild("AppState") ?? node;

            var name = state.GetValue("name")?.Trim();
            var installDirName = state.GetValue("installdir")?.Trim();
            var stateFlags = state.GetValue("StateFlags")?.Trim();

            if (string.IsNullOrEmpty(name))
            {
                name = string.IsNullOrEmpty(installDirName)
                    ? $"未知游戏 ({appId ?? Path.GetFileNameWithoutExtension(manifest)})"
                    : installDirName;
            }

            // 目录缺失时回退到 installdir 名称，保证列表仍然可见
            var installDir = !string.IsNullOrEmpty(installDirName)
                ? Path.GetFullPath(Path.Combine(commonDir, installDirName))
                : commonDir;

            var note = $"库: {steamApps}";

            if (!string.IsNullOrEmpty(stateFlags) && stateFlags != "4")
            {
                // StateFlags 4 = 已完整安装。其他值说明还在更新/预分配。
                note += $" | StateFlags={stateFlags}";
            }

            var entry = new GameEntry
            {
                Name = name!,
                NormalizedName = NameMatcher.Normalize(name!),
                AppId = appId,
                InstallDir = installDir,
                Source = GameSource.Steam,
                Note = note,
            };

            if (!Directory.Exists(installDir))
            {
                entry.Note += " | 目录不存在";
            }

            games.Add(entry);
        }

        return games;
    }

    /// <summary>打开 Steam 库文件夹，方便用户核对安装位置。</summary>
    public static void OpenInExplorer(string path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path)) return;
            if (!Directory.Exists(path)) return;

            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"\"{path}\"",
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            Log.Error($"打开目录失败: {path}", ex);
        }
    }
}

using System.Diagnostics;
using System.Text.RegularExpressions;
using GalPatchManager.Models;

namespace GalPatchManager.Services;

/// <summary>EXE 可信性判定结果。</summary>
public sealed class ExeTrustCheck
{
    public string FilePath { get; init; } = "";

    public string Sha256 { get; set; } = "";

    public bool IsTrusted { get; set; }

    public bool RuleMatched { get; set; }

    public string RuleName { get; set; } = "";

    /// <summary>该规则是否提供了静默安装参数。</summary>
    public bool HasSilentArguments { get; set; }

    public string SilentArguments { get; set; } = "";

    /// <summary>是否满足“自动运行”的全部条件。</summary>
    public bool AutoRunAllowed { get; set; }

    /// <summary>判定说明，直接展示给用户。</summary>
    public string Reason { get; set; } = "";
}

/// <summary>
/// EXE 补丁的可信规则判定。
///
/// 安全原则：
///   * 绝不猜测静默安装参数。没有用户配置的规则就不会自动运行。
///   * 只凭文件名/扩展名不算可信，必须命中用户配置的规则，并且满足其中的 SHA-256 或来源域名约束。
///   * 默认还会叠加“即使可信也要用户确认”。
/// </summary>
public static class ExeTrustPolicy
{
    /// <summary>
    /// 判定一个 EXE 补丁。
    /// </summary>
    /// <param name="filePath">EXE 路径。</param>
    /// <param name="sha256">已计算的 SHA-256（可传空，内部会计算）。</param>
    /// <param name="settings">设置。</param>
    /// <param name="sourceHost">
    /// 该补丁的来源域名（若来自搜索结果，可传入候选网址的 Host，用于域名白名单校验）。
    /// 手动放入下载目录时通常为 null。
    /// </param>
    public static ExeTrustCheck Evaluate(
        string filePath,
        string? sha256,
        AppSettings settings,
        string? sourceHost = null)
    {
        var check = new ExeTrustCheck
        {
            FilePath = filePath,
            Sha256 = sha256 ?? "",
        };

        if (string.IsNullOrWhiteSpace(check.Sha256) && File.Exists(filePath))
        {
            try
            {
                check.Sha256 = FileHash.Sha256(filePath);
            }
            catch (Exception ex)
            {
                check.Reason = $"无法计算 SHA-256：{ex.Message}";
                return check;
            }
        }

        var fileName = Path.GetFileName(filePath);

        foreach (var rule in settings.ExeTrustRules)
        {
            if (rule is null) continue;

            // ---- 1) 文件名通配 ----
            if (!string.IsNullOrWhiteSpace(rule.FileNamePattern) &&
                !WildcardMatch(rule.FileNamePattern.Trim(), fileName))
            {
                continue;
            }

            // ---- 2) 必须至少有一条实质性约束（哈希 或 来源域名）----
            var hasHashConstraint = rule.AllowedSha256.Any(h => !string.IsNullOrWhiteSpace(h));
            var hasHostConstraint = rule.AllowedHosts.Any(h => !string.IsNullOrWhiteSpace(h));

            if (!hasHashConstraint && !hasHostConstraint)
            {
                // 只有文件名匹配不足以自动运行——这一点非常重要，避免规则被滥用
                continue;
            }

            // ---- 3) 哈希校验 ----
            if (hasHashConstraint)
            {
                var allowed = rule.AllowedSha256
                    .Where(h => !string.IsNullOrWhiteSpace(h))
                    .Select(h => h.Trim().Replace(" ", "").Replace("-", "").ToLowerInvariant())
                    .ToList();

                if (!allowed.Contains(check.Sha256.ToLowerInvariant()))
                {
                    continue;
                }
            }

            // ---- 4) 来源域名校验（提供了来源时才校验）----
            if (hasHostConstraint)
            {
                if (string.IsNullOrWhiteSpace(sourceHost)) continue;

                var ok = rule.AllowedHosts
                    .Where(h => !string.IsNullOrWhiteSpace(h))
                    .Select(h => h.Trim().TrimStart('.'))
                    .Any(h => sourceHost.Equals(h, StringComparison.OrdinalIgnoreCase) ||
                              sourceHost.EndsWith("." + h, StringComparison.OrdinalIgnoreCase));

                if (!ok) continue;
            }

            // ---- 命中规则 ----
            check.RuleMatched = true;
            check.IsTrusted = true;
            check.RuleName = string.IsNullOrWhiteSpace(rule.Name) ? "(未命名规则)" : rule.Name;
            check.SilentArguments = rule.SilentArguments ?? "";
            check.HasSilentArguments = check.SilentArguments.Trim().Length > 0;

            var silentOk = rule.Mode == ExeRunMode.Silent && check.HasSilentArguments;

            check.AutoRunAllowed = settings.AutoRunTrustedExe && !settings.AlwaysConfirmExe && silentOk;

            var parts = new List<string>
            {
                $"命中可信规则「{check.RuleName}」",
                "SHA-256 校验通过",
            };

            if (!string.IsNullOrWhiteSpace(sourceHost)) parts.Add($"来源域名 {sourceHost} 在白名单内");

            if (rule.Mode == ExeRunMode.Silent && !check.HasSilentArguments)
            {
                parts.Add("但该规则未配置静默参数，只能交互式运行");
            }
            else if (rule.Mode == ExeRunMode.Silent)
            {
                parts.Add($"静默参数：{check.SilentArguments}");
            }
            else
            {
                parts.Add("规则为交互式安装");
            }

            if (settings.AlwaysConfirmExe) parts.Add("当前设置为“始终要求确认”");
            if (!settings.AutoRunTrustedExe) parts.Add("自动运行开关未开启");

            check.Reason = string.Join("；", parts);
            return check;
        }

        check.IsTrusted = false;
        check.AutoRunAllowed = false;

        check.Reason = settings.ExeTrustRules.Count == 0
            ? "未配置任何可信规则，需要你手动确认后才能运行。"
            : "未命中任何可信规则（或哈希/来源校验未通过），需要你手动确认后才能运行。";

        if (check.Sha256.Length >= 16)
        {
            check.Reason += $" [SHA-256 {check.Sha256[..16]}…]";
        }
        else if (check.Sha256.Length > 0)
        {
            check.Reason += $" [SHA-256 {check.Sha256}]";
        }

        return check;
    }

    /// <summary>通配符匹配（* 和 ?）。</summary>
    private static bool WildcardMatch(string pattern, string input)
    {
        var regex = "^" + Regex.Escape(pattern)
            .Replace(@"\*", ".*", StringComparison.Ordinal)
            .Replace(@"\?", ".", StringComparison.Ordinal) + "$";

        return Regex.IsMatch(input, regex, RegexOptions.IgnoreCase);
    }
}

/// <summary>一次 EXE 补丁运行的记录。</summary>
public sealed class ExeRunResult
{
    public bool Started { get; set; }

    public bool? ExitedSuccessfully { get; set; }

    public int? ExitCode { get; set; }

    public string Message { get; set; } = "";
}

/// <summary>
/// EXE 补丁运行器。
/// 只负责按既定方式启动进程并回报结果，不做任何提权、不绕过 UAC。
/// </summary>
public static class ExePatchRunner
{
    /// <summary>
    /// 运行 EXE 补丁。
    /// </summary>
    /// <param name="silentArguments">
    /// 由可信规则提供的静默参数。为空时以交互方式运行（弹出安装向导让用户自己操作）。
    /// 本程序不会自行拼接或猜测 {NSIS} /S 之类的参数。
    /// </param>
    /// <param name="waitForExit">是否等待其结束并检查退出码。</param>
    public static async Task<ExeRunResult> RunAsync(
        string exePath,
        string? silentArguments,
        bool waitForExit,
        CancellationToken cancellationToken = default)
    {
        var result = new ExeRunResult();

        if (!File.Exists(exePath))
        {
            result.Message = $"文件不存在: {exePath}";
            return result;
        }

        if (!exePath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            result.Message = "只支持运行 .exe 补丁。";
            return result;
        }

        var arguments = (silentArguments ?? "").Trim();
        var interactive = arguments.Length == 0;

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = exePath,
                Arguments = arguments,
                // 关键：必须用 ShellExecute，才能触发 Windows 自身的 UAC / SmartScreen 提示。
                // 程序绝不会绕过这些系统权限提示。
                UseShellExecute = true,
                WorkingDirectory = Path.GetDirectoryName(exePath) ?? Environment.CurrentDirectory,
            };

            var process = Process.Start(psi);

            if (process is null)
            {
                result.Message = "进程启动失败（Process.Start 返回 null）。";
                return result;
            }

            result.Started = true;
            result.Message = interactive
                ? "已以交互方式启动安装程序，请在向导中完成安装。"
                : $"已使用静默参数「{arguments}」启动安装程序。";

            Log.Info($"启动 EXE 补丁: {exePath} 参数: {(interactive ? "(无，交互式)" : arguments)}");

            if (!waitForExit)
            {
                return result;
            }

            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

            result.ExitCode = process.ExitCode;
            result.ExitedSuccessfully = process.ExitCode == 0;

            if (process.ExitCode == 0)
            {
                result.Message += " 安装程序已正常退出（退出码 0）。";
            }
            else
            {
                result.Message += $" 安装程序退出码为 {process.ExitCode}，可能未安装成功，请检查。";
            }

            Log.Info($"EXE 补丁退出: {exePath} -> 退出码 {process.ExitCode}");

            process.Dispose();
            return result;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            result.Message = $"启动失败: {ex.Message}";
            Log.Error($"启动 EXE 失败: {exePath}", ex);
            return result;
        }
    }
}

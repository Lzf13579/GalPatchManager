using System.Text.Json.Serialization;

namespace GalPatchManager.Models;

/// <summary>游戏来源。</summary>
public enum GameSource
{
    /// <summary>从 Steam 库自动识别。</summary>
    Steam = 0,

    /// <summary>用户手动添加。</summary>
    Manual = 1,
}

/// <summary>补丁任务状态。</summary>
public enum PatchState
{
    /// <summary>已发现，尚未判断下载是否结束。</summary>
    Detected = 0,

    /// <summary>仍在写入或大小不稳定。</summary>
    Downloading = 1,

    /// <summary>下载完成，等待匹配游戏。</summary>
    Downloaded = 2,

    /// <summary>已匹配到游戏，等待用户确认。</summary>
    Matched = 3,

    /// <summary>安装中。</summary>
    Installing = 4,

    /// <summary>安装成功。</summary>
    Completed = 5,

    /// <summary>失败。</summary>
    Failed = 6,

    /// <summary>用户忽略 / 重复，不再处理。</summary>
    Skipped = 7,
}

/// <summary>安装历史记录的一条结果。</summary>
public enum InstallStatus
{
    Success = 0,
    Failed = 1,
    RolledBack = 2,
    Rejected = 3,
}

/// <summary>补丁来源类型。</summary>
public enum InstallSource
{
    Archive = 0,
    Exe = 1,
    Folder = 2,
}

/// <summary>EXE 安装程序的运行模式。</summary>
public enum ExeRunMode
{
    /// <summary>由用户配置的静默参数运行。</summary>
    Silent = 0,

    /// <summary>直接交互式运行，让用户手动点下一步。默认模式。</summary>
    Interactive = 1,
}

/// <summary>压缩包内一个条目的角色，用于推断目录前缀。</summary>
public enum EntryRole
{
    /// <summary>根目录下的散装文件（通常是补丁本体）。</summary>
    ArchiveFile = 0,

    /// <summary>位于某个一级子目录内。</summary>
    NestedFile = 1,

    /// <summary>目录条目。</summary>
    Directory = 2,
}

/// <summary>一个已识别的游戏。</summary>
public sealed class GameEntry
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>用于显示的完整名称。</summary>
    public string Name { get; set; } = "";

    /// <summary>去掉常见后缀的规范化名称，用于模糊匹配。</summary>
    public string NormalizedName { get; set; } = "";

    /// <summary>Steam AppID；手动添加的游戏为 null。</summary>
    public string? AppId { get; set; }

    public string InstallDir { get; set; } = "";

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public GameSource Source { get; set; } = GameSource.Manual;

    /// <summary>补充说明，例如 Steam 库路径或 .acf 文件路径。</summary>
    public string Note { get; set; } = "";

    /// <summary>用于匹配用户配置的关键词规则（可为空）。</summary>
    public List<string> MatchKeywords { get; set; } = new();

    /// <summary>用户手动指定的游戏类型（为空表示未指定）。</summary>
    public string CustomType { get; set; } = "";

    /// <summary>
    /// 启发式自动识别出的类型（扫描引擎特征文件得到）。
    /// 只是"尽量猜"的结果，用户指定优先。
    /// </summary>
    public string DetectedType { get; set; } = "";

    /// <summary>自动识别的依据说明，用于界面展示。</summary>
    public string DetectedTypeReason { get; set; } = "";

    /// <summary>最后一次安装补丁的时间。</summary>
    public DateTime? LastPatchedAt { get; set; }

    /// <summary>已安装补丁的数量（用于去重提示）。</summary>
    public int InstalledPatchCount { get; set; }

    [JsonIgnore]
    public string DisplaySource => Source == GameSource.Steam
        ? (string.IsNullOrEmpty(AppId) ? "Steam" : $"Steam ({AppId})")
        : "手动添加";

    [JsonIgnore]
    public string DisplayAppId => string.IsNullOrEmpty(AppId) ? "—" : AppId!;

    public override string ToString() => $"{Name} [{DisplaySource}]";
}

/// <summary>一条网络搜索得到的候选补丁网址。</summary>
public sealed class PatchCandidate
{
    public string Title { get; set; } = "";

    public string Url { get; set; } = "";

    /// <summary>来源站点（从 URL 推断的域名）。</summary>
    public string Site { get; set; } = "";

    /// <summary>搜索服务/接口返回的摘要。</summary>
    public string Snippet { get; set; } = "";

    /// <summary>产生该结果的搜索引擎名称。</summary>
    public string Engine { get; set; } = "";

    /// <summary>是否命中用户白名单。</summary>
    public bool IsWhitelisted { get; set; }

    /// <summary>
    /// 恒为 false。
    /// 搜索结果只是候选网址，程序不会把它当作已经验证过的补丁文件。
    /// </summary>
    public bool IsVerified => false;

    public DateTime FoundAt { get; set; } = DateTime.Now;

    public override string ToString() => $"{Title} - {Url}";
}

/// <summary>可信 EXE 安装规则。</summary>
public sealed class ExeTrustRule
{
    public string Name { get; set; } = "";

    /// <summary>匹配文件名（支持通配符，例如 "patch*.exe"）。为空表示不限制。</summary>
    public string FileNamePattern { get; set; } = "";

    /// <summary>允许的域名列表，用于校验“来源”备注。为空表示不限制。</summary>
    public List<string> AllowedHosts { get; set; } = new();

    /// <summary>允许的 SHA-256（十六进制，不区分大小写）。为空表示不做哈希限制。</summary>
    public List<string> AllowedSha256 { get; set; } = new();

    /// <summary>
    /// 静默安装参数，例如 "/S" 或 "/silent /norestart"。
    /// 程序绝不会自行猜测这些参数：为空时只能以交互方式运行。
    /// </summary>
    public string SilentArguments { get; set; } = "";

    public ExeRunMode Mode { get; set; } = ExeRunMode.Interactive;
}

/// <summary>关键词匹配规则。</summary>
public sealed class MatchRuleEntry
{
    public string Keyword { get; set; } = "";

    /// <summary>命中后映射到的游戏 Id。</summary>
    public string GameId { get; set; } = "";

    /// <summary>是否启用。</summary>
    public bool Enabled { get; set; } = true;
}

/// <summary>压缩包内的一个条目。</summary>
public sealed class ArchiveEntryInfo
{
    public string FullPath { get; set; } = "";

    public long Size { get; set; }

    public bool IsDirectory { get; set; }

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public EntryRole Role { get; set; } = EntryRole.NestedFile;

    /// <summary>相对压缩包根的前缀（去掉根目录名之后的部分）。</summary>
    public string RelativePath { get; set; } = "";
}

/// <summary>安装计划的单个步骤（用于界面预览与备份）。</summary>
public sealed class InstallStep
{
    public string Step { get; set; } = "";

    public string Target { get; set; } = "";

    public string Detail { get; set; } = "";

    public bool IsBlocking { get; set; }
}

/// <summary>备份条目。</summary>
public sealed class BackupEntry
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>被覆盖前原文件的绝对路径。</summary>
    public string OriginalPath { get; set; } = "";

    /// <summary>备份文件所在路径。</summary>
    public string BackupPath { get; set; } = "";

    public string GameId { get; set; } = "";

    public string PatchKey { get; set; } = "";

    public long Size { get; set; }

    public string Sha256 { get; set; } = "";

    public DateTime CreatedAt { get; set; } = DateTime.Now;
}

/// <summary>安装历史记录。</summary>
public sealed class InstallRecord
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string PatchKey { get; set; } = "";

    public string GameId { get; set; } = "";

    public string GameName { get; set; } = "";

    public string PatchFileName { get; set; } = "";

    public string PatchPath { get; set; } = "";

    public string Sha256 { get; set; } = "";

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public InstallSource Source { get; set; } = InstallSource.Archive;

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public InstallStatus Status { get; set; } = InstallStatus.Success;

    public int FilesCopied { get; set; }

    public int FilesBackedUp { get; set; }

    public long BytesCopied { get; set; }

    public string Message { get; set; } = "";

    public DateTime StartedAt { get; set; } = DateTime.Now;

    public DateTime FinishedAt { get; set; } = DateTime.Now;

    /// <summary>备份批次 Id，可用于一键还原。</summary>
    public string BackupBatchId { get; set; } = "";

    public override string ToString() => $"{GameName} / {PatchFileName}";
}

/// <summary>待处理的下载/补丁条目。</summary>
public sealed class PatchDownloadItem
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string FilePath { get; set; } = "";

    public string FileName { get; set; } = "";

    public long Size { get; set; }

    public string Sha256 { get; set; } = "";

    /// <summary>
    /// 下载完成判定标记。
    /// 只有大小连续稳定、且文件能独立打开并完整读取后才会置为 true。
    /// </summary>
    public bool IsComplete { get; set; }

    public int StabilityChecks { get; set; }

    public DateTime FirstSeen { get; set; } = DateTime.Now;

    public DateTime LastChanged { get; set; } = DateTime.Now;

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public PatchState State { get; set; } = PatchState.Detected;

    /// <summary>匹配到的游戏 Id；null 表示未确定。</summary>
    public string? MatchedGameId { get; set; }

    /// <summary>匹配依据，例如 "AppID 1234" / "关键词 galgame" / "用户选择"。</summary>
    public string MatchReason { get; set; } = "";

    public bool IsDuplicate { get; set; }

    public string DuplicateOfRecordId { get; set; } = "";

    public string Error { get; set; } = "";

    /// <summary>状态的中文说明，供界面显示。</summary>
    [JsonIgnore]
    public string StateText => State switch
    {
        PatchState.Detected => "已发现",
        PatchState.Downloading => "下载中",
        PatchState.Downloaded => "下载完成",
        PatchState.Matched => "已匹配",
        PatchState.Installing => "安装中",
        PatchState.Completed => "已完成",
        PatchState.Failed => "失败",
        PatchState.Skipped => "已忽略",
        _ => State.ToString(),
    };

    /// <summary>该条目是否为 .exe 补丁。</summary>
    public bool IsExe { get; set; }

    /// <summary>命中的可信规则名称。</summary>
    public string TrustedRuleName { get; set; } = "";

    /// <summary>是否为可信 EXE（满足规则 + 哈希校验）。</summary>
    public bool IsTrustedExe { get; set; }

    /// <summary>是否允许自动运行。</summary>
    public bool AutoRunAllowed { get; set; }

    public override string ToString() => FileName;
}

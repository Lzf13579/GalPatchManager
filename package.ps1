# 打包脚本：构建 → 自检 → 发布为单个 .exe → 生成 .zip 分发包
#
# 用法（在仓库根目录执行）：
#   powershell -ExecutionPolicy Bypass -File package.ps1
#   powershell -ExecutionPolicy Bypass -File package.ps1 -OutputDir dist
#   powershell -ExecutionPolicy Bypass -File package.ps1 -NoCompress   # 单文件但不做内部压缩（启动更快、体积更大）
#
# 产物：
#   <OutputDir>\GalPatchManager-v1.0.0-win-x64\GalPatchManager.exe   ← 唯一可执行文件
#   <OutputDir>\GalPatchManager-v1.0.0-win-x64\使用说明.txt
#   <OutputDir>\GalPatchManager-v1.0.0-win-x64.zip                   ← 分发包
#
# 说明：
#   * 单个 .exe 必须是**自包含**发布：框架依赖模式下 SharpCompress.dll 会作为独立文件留在旁边，
#     无法整合进 exe。自包含单文件把一个完整的 .NET 运行时打进 exe，因此体积较大（约 69 MB）。
#   * 目标机器无需安装 .NET；仅支持 Windows 10/11 x64。

[CmdletBinding()]
param(
    [string]$Configuration = 'Release',
    [string]$Runtime = 'win-x64',
    [string]$OutputDir = 'dist',
    [string]$DotnetPath,

    # 单文件内部压缩（体积小、首次启动稍慢）
    [bool]$Compress = $true,

    # 跳过自检
    [switch]$SkipSelfTest
)

$ErrorActionPreference = 'Stop'

$root = $PSScriptRoot
$project = Join-Path $root 'src/GalPatchManager/GalPatchManager.csproj'

if (-not (Test-Path $project)) { throw "找不到项目文件: $project" }

# ---------------------------------------------------------------- 版本号
# 注意：csproj 是 UTF-8（无 BOM），Windows PowerShell 的 Get-Content 默认按本地代码页读取，
# 会把中文注释读成乱码导致 XML 解析失败，所以这里显式按 UTF-8 读取。
[xml]$csproj = (Get-Content $project -Raw -Encoding UTF8)
$version = ($csproj.Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1)
if (-not $version) { $version = '1.0.0' }

$pkgName = "GalPatchManager-v$version-$Runtime"
$stageDir = Join-Path $root (Join-Path $OutputDir $pkgName)
$publishDir = Join-Path $root (Join-Path $OutputDir '_publish')
$zipPath = Join-Path $root (Join-Path $OutputDir "$pkgName.zip")

Write-Host "==> 版本: $version   包名: $pkgName" -ForegroundColor Cyan

# ---------------------------------------------------------------- dotnet
function Resolve-Dotnet {
    param([string]$Explicit)

    if ($Explicit) {
        if (-not (Test-Path $Explicit)) { throw "指定的 dotnet 不存在: $Explicit" }
        return $Explicit
    }

    $cmd = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }

    foreach ($c in @(
            (Join-Path $env:ProgramFiles 'dotnet/dotnet.exe'),
            (Join-Path ${env:ProgramFiles(x86)} 'dotnet/dotnet.exe'),
            (Join-Path $env:LOCALAPPDATA 'Microsoft/dotnet/dotnet.exe'),
            (Join-Path $root '_dotnet-dl/dotnet/dotnet.exe'))) {
        if ($c -and (Test-Path $c)) { return $c }
    }

    throw "未找到 dotnet。请安装 .NET 8 SDK，或用 -DotnetPath 指定路径。"
}

$dotnet = Resolve-Dotnet -Explicit $DotnetPath

# ---------------------------------------------------------------- 清理
foreach ($d in @($stageDir, $publishDir)) {
    if (Test-Path $d) { Remove-Item $d -Recurse -Force }
}

if (Test-Path $zipPath) { Remove-Item $zipPath -Force }

New-Item -ItemType Directory -Force -Path $stageDir | Out-Null

# ---------------------------------------------------------------- 自检
if (-not $SkipSelfTest) {
    Write-Host "`n==> 构建并自检" -ForegroundColor Cyan

    & $dotnet build $project -c $Configuration --nologo
    if ($LASTEXITCODE -ne 0) { throw "构建失败。" }

    $dll = Join-Path $root "src/GalPatchManager/bin/$Configuration/net8.0-windows/GalPatchManager.dll"

    & $dotnet $dll --selftest
    if ($LASTEXITCODE -ne 0) { throw "自检未通过，已中止打包。" }

    Write-Host "    自检通过。" -ForegroundColor Green
}

# ---------------------------------------------------------------- 单文件发布
Write-Host "`n==> 发布单个 .exe（自包含, $Runtime）" -ForegroundColor Cyan

$publishArgs = @(
    'publish', $project,
    '-c', $Configuration,
    '-r', $Runtime,
    '--self-contained', 'true',
    '-o', $publishDir,
    '-p:PublishSingleFile=true',
    '-p:IncludeNativeLibrariesForSelfExtract=true',
    '-p:DebugType=none',
    '--nologo'
)

if ($Compress) {
    $publishArgs += '-p:EnableCompressionInSingleFile=true'
}

& $dotnet @publishArgs
$publishExit = $LASTEXITCODE

if ($publishExit -ne 0) {
    # 常见原因：上一次的 publish 目录被占用（旧进程没退干净 / 杀软扫描中）。
    # 清理后重试一次，仍失败才中止。
    Write-Host "[警告] 发布失败（退出码 $publishExit），清理输出目录后重试一次..." -ForegroundColor Yellow

    Get-Process -Name GalPatchManager -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
    Start-Sleep -Seconds 2

    if (Test-Path $publishDir) {
        Remove-Item $publishDir -Recurse -Force -ErrorAction SilentlyContinue
    }

    & $dotnet @publishArgs
    $publishExit = $LASTEXITCODE
}

if ($publishExit -ne 0) { throw "发布失败（退出码 $publishExit）。" }

# 清掉退出码，避免影响后续判断
$global:LASTEXITCODE = 0

# ---------------------------------------------------------------- 组装分发包
$exe = Join-Path $publishDir 'GalPatchManager.exe'

if (-not (Test-Path $exe)) { throw "发布目录中没有找到 GalPatchManager.exe" }

$extra = Get-ChildItem $publishDir -File | Where-Object { $_.Name -ne 'GalPatchManager.exe' }

if ($extra) {
    Write-Host "`n[提示] 发布目录还有额外文件，已一并打包：" -ForegroundColor Yellow
    $extra | ForEach-Object { Write-Host "        $($_.Name)" }

    Copy-Item (Join-Path $publishDir '*') $stageDir -Recurse -Force
}
else {
    Copy-Item $exe $stageDir -Force
}

$stagedExe = Join-Path $stageDir 'GalPatchManager.exe'
$sizeMb = [math]::Round((Get-Item $stagedExe).Length / 1MB, 1)

# 使用说明
$readme = @"
游戏补丁安装器 v$version
========================================

【运行】
  双击 GalPatchManager.exe 即可，无需安装 .NET 运行时。
  仅支持 Windows 10 / 11 x64。

  首次运行若出现 SmartScreen 提示，点「更多信息」→「仍要运行」。
  （程序未做代码签名，这是正常现象。）

【配置与数据位置】
  %AppData%\GalPatchManager\
      config.json      设置
      history.json     安装历史
      downloads.json   待处理补丁
      baseline.json    各监控目录的「已存在文件」基线
      DropFolder\      默认监控目录（把补丁丢进这里即可）
      Logs\            按天日志
      Backups\         覆盖前的文件备份（默认保留 30 天）
      Temp\            解压临时目录
      InstalledPatches\ 安装成功后归档的补丁

【下载监控说明（重要）】
  默认监控的是 %AppData%\GalPatchManager\DropFolder 这个「放这里」文件夹，
  而不是系统「下载」目录 —— 因为系统下载目录里通常堆满了各种软件安装包。

  默认只处理「新下载」的文件：第一次监控某目录时，其中已存在的文件会被
  全部登记为基线并忽略，之后只有新增或内容有变化的文件才会进入待处理列表。
  程序关闭期间下载的补丁也不会漏（修改时间晚于基线建立时间）。

  设置页可以看到基线状态，也可以「重新建立基线」或「清除基线」。

【自检】
  命令行执行：
      GalPatchManager.exe --selftest
  退出码 0 表示全部通过，报告写入 %AppData%\GalPatchManager\selftest-result.txt

【建议的使用顺序】
  1. 设置 → 确认「下载目录」（默认 DropFolder）并保存
  2. 游戏列表 → 刷新列表（自动扫描 Steam 全部库），其他游戏手动添加
  3. 查找补丁 → 选择游戏 → 查找补丁（默认浏览器搜索，不伪造结果）
  4. 把补丁下载/拖到监控目录，程序自动识别下载完成
  5. 待处理补丁 → 确认匹配的游戏 → 解压并预览 → 安装到游戏

【重要安全说明】
  * 程序不会自动从搜索结果抓取补丁文件，需要你确认来源后自行下载。
  * 搜索结果只是候选网址，不代表已验证的补丁文件。
  * 覆盖游戏文件前会自动备份，安装失败会自动回滚。
  * .exe 补丁默认必须经你确认才运行；自动运行需要同时满足：
    命中你配置的可信规则 + SHA-256/来源域名校验通过 + 规则里显式配置了
    静默安装参数 + 你手动开启了自动运行开关。
  * 程序不会猜测静默安装参数，也不会绕过 Windows 的 UAC / SmartScreen 提示。

【不支持】
  加密压缩包（需密码）、分卷压缩包（.part1.rar / .7z.001）。
  遇到时会明确报错，不会静默失败。

完整文档见项目 README.md。
"@

$readmePath = Join-Path $stageDir '使用说明.txt'
# 带 BOM，保证记事本与 PowerShell 都正确显示中文
[IO.File]::WriteAllText($readmePath, $readme, (New-Object Text.UTF8Encoding($true)))

# ---------------------------------------------------------------- 压缩
Write-Host "`n==> 生成 zip" -ForegroundColor Cyan

Compress-Archive -Path $stageDir -DestinationPath $zipPath -CompressionLevel Optimal -Force

# 清理中间发布目录
if (Test-Path $publishDir) { Remove-Item $publishDir -Recurse -Force }

$zipMb = [math]::Round((Get-Item $zipPath).Length / 1MB, 1)

Write-Host ""
Write-Host "完成。" -ForegroundColor Green
Write-Host "  单文件 exe : $stagedExe  ($sizeMb MB)"
Write-Host "  分发包 zip : $zipPath  ($zipMb MB)"
Write-Host "  使用说明   : $readmePath"

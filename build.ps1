# 构建 / 打包脚本
#
# 用法（在仓库根目录执行）：
#   pwsh -File build.ps1                  # Debug 构建 + 运行自检
#   pwsh -File build.ps1 -Configuration Release -Publish
#   pwsh -File build.ps1 -Publish -SelfContained
#
# 说明：
#   * 脚本会自动寻找 dotnet。若 PATH 中没有，可用 -DotnetPath 显式指定。
#   * 若你需要通过代理访问 NuGet，脚本会沿用当前的 HTTP_PROXY / HTTPS_PROXY 环境变量。

[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug',

    # 是否执行 dotnet publish 生成可分发的输出
    [switch]$Publish,

    # 自包含发布（不要求目标机器预装 .NET 8 运行时）。体积会大很多。
    [switch]$SelfContained,

    # 单文件发布
    [switch]$SingleFile,

    # 运行时标识
    [string]$Runtime = 'win-x64',

    # 显式指定 dotnet.exe 路径
    [string]$DotnetPath,

    # 跳过自检
    [switch]$SkipSelfTest,

    [string]$OutputDir = 'publish'
)

$ErrorActionPreference = 'Stop'

$root = $PSScriptRoot
$project = Join-Path $root 'src/GalPatchManager/GalPatchManager.csproj'

if (-not (Test-Path $project)) {
    throw "找不到项目文件: $project"
}

# ---------------------------------------------------------------- 定位 dotnet
function Resolve-Dotnet {
    param([string]$Explicit)

    if ($Explicit) {
        if (-not (Test-Path $Explicit)) { throw "指定的 dotnet 不存在: $Explicit" }
        return $Explicit
    }

    $cmd = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }

    # 常见安装位置兜底
    $candidates = @(
        (Join-Path $env:ProgramFiles 'dotnet/dotnet.exe'),
        (Join-Path ${env:ProgramFiles(x86)} 'dotnet/dotnet.exe'),
        (Join-Path $env:LOCALAPPDATA 'Microsoft/dotnet/dotnet.exe'),
        (Join-Path $root '_dotnet-dl/dotnet/dotnet.exe')
    )

    foreach ($c in $candidates) {
        if ($c -and (Test-Path $c)) { return $c }
    }

    throw "未找到 dotnet。请安装 .NET 8 SDK，或用 -DotnetPath 指定路径。"
}

$dotnet = Resolve-Dotnet -Explicit $DotnetPath

Write-Host "==> dotnet: $dotnet" -ForegroundColor Cyan
& $dotnet --version

# ---------------------------------------------------------------- 构建
Write-Host "`n==> dotnet build ($Configuration)" -ForegroundColor Cyan

& $dotnet build $project -c $Configuration --nologo
if ($LASTEXITCODE -ne 0) { throw "构建失败。" }

# ---------------------------------------------------------------- 自检
if (-not $SkipSelfTest) {
    Write-Host "`n==> 运行自检 (--selftest)" -ForegroundColor Cyan

    $tfm = 'net8.0-windows'
    $dll = Join-Path $root "src/GalPatchManager/bin/$Configuration/$tfm/GalPatchManager.dll"

    if (-not (Test-Path $dll)) { throw "找不到构建产物: $dll" }

    & $dotnet $dll --selftest
    $code = $LASTEXITCODE

    $report = Join-Path $env:APPDATA 'GalPatchManager/selftest-result.txt'
    if (Test-Path $report) {
        Get-Content $report -Encoding UTF8 | Select-Object -First 4 | ForEach-Object { Write-Host "    $_" }
    }

    if ($code -ne 0) { throw "自检未通过（退出码 $code），已中止打包。" }

    Write-Host "    自检通过。" -ForegroundColor Green
}

# ---------------------------------------------------------------- 发布
if ($Publish) {
    Write-Host "`n==> dotnet publish ($Configuration, $Runtime)" -ForegroundColor Cyan

    $out = Join-Path $root $OutputDir

    $args = @(
        'publish', $project,
        '-c', $Configuration,
        '-r', $Runtime,
        '-o', $out,
        '--nologo'
    )

    if ($SelfContained) {
        $args += '--self-contained', 'true'

        if ($SingleFile) {
            $args += '-p:PublishSingleFile=true'
            $args += '-p:IncludeNativeLibrariesForSelfExtract=true'
        }
    }
    else {
        $args += '--self-contained', 'false'
    }

    & $dotnet @args
    if ($LASTEXITCODE -ne 0) { throw "发布失败。" }

    Write-Host "`n==> 输出目录: $out" -ForegroundColor Green
    Get-ChildItem $out | Select-Object Name, @{ n = 'Size(KB)'; e = { [math]::Round($_.Length / 1KB, 1) } } |
        Format-Table -AutoSize
}

Write-Host "`n完成。" -ForegroundColor Green

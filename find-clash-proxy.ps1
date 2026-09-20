<#
.SYNOPSIS
    发现本机 Clash / Clash Verge / mihomo 的代理地址，测试它能否访问 Steam，并写入「游戏补丁安装器」配置。

.DESCRIPTION
    做三件事：
      1. 找到 Clash 的代理端口（多个来源交叉验证：注册表系统代理 + Clash Verge 配置文件 + 常见默认端口）
      2. 逐个测试这些地址能否**真正**访问 Steam 商店接口
         （只做 TCP 连通性是不够的：实测过「CONNECT 隧道能建起来但 TLS 握手失败」的代理）
      3. 把可用地址写进 %AppData%\GalPatchManager\config.json：
         CustomProxyUrl = <地址>，NetworkMode = CustomProxy

.PARAMETER App
    程序 exe 路径。默认自动在脚本目录和 dist 目录里找 GalPatchManager.exe。

.PARAMETER NoApply
    只探测并打印，不修改配置文件。

.PARAMETER Address
    手动指定代理地址（如 127.0.0.1:7897），跳过自动发现。

.PARAMETER Launch
    写入配置后顺便启动程序。

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File find-clash-proxy.ps1
    powershell -ExecutionPolicy Bypass -File find-clash-proxy.ps1 -NoApply
    powershell -ExecutionPolicy Bypass -File find-clash-proxy.ps1 -Address 127.0.0.1:7897 -Launch

.NOTES
    本脚本只读取 Clash 配置、只改动本程序的 config.json（改前自动备份）。
    不会修改系统代理设置，也不会改动 Clash 的任何配置。
#>

[CmdletBinding()]
param(
    [string]$App,
    [switch]$NoApply,
    [string]$Address,
    [switch]$Launch
)

# 用 Continue：单个探测失败不应该让整个脚本中断
$ErrorActionPreference = 'Continue'

function Write-Head($t) { Write-Host "`n==> $t" -ForegroundColor Cyan }
function Write-Ok($t)   { Write-Host "    [OK]   $t" -ForegroundColor Green }
function Write-Bad($t)  { Write-Host "    [FAIL] $t" -ForegroundColor Red }
function Write-Info($t) { Write-Host "    $t" -ForegroundColor Gray }

$results = New-Object System.Collections.Generic.List[object]

<#
    证书校验回调必须用「真正的 .NET 委托」，不能用 PowerShell 脚本块。
    原因：SslStream 在 TLS 线程上回调，那里没有 PowerShell 运行空间，
    用 { $true } 会抛「此线程中没有可用于运行脚本的运行空间」。
    （曾经就是这么写的，导致直连路径必然失败。）

    这里用一个纯 C# 静态方法作为回调，任何线程都能安全调用。
    本脚本只做连通性测试、不传敏感数据；TLS 加密协商仍照常进行，
    这里只是不因代理证书/自签名而中断测试。
#>
if (-not ('NetHelper' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;

public static class NetHelper
{
    public static RemoteCertificateValidationCallback AcceptAll()
    {
        return new RemoteCertificateValidationCallback(
            delegate(object sender, X509Certificate cert, X509Chain chain, SslPolicyErrors errors)
            {
                return true;
            });
    }
}
'@ -ErrorAction SilentlyContinue
}

# 提前构造好，避免每次握手都重新反射创建
$script:CertCallback = [NetHelper]::AcceptAll()

$steamHost = 'store.steampowered.com'

<#
    剥掉 PowerShell/.NET 的包装异常，取最内层真正的错误信息。
    Wait() / Invoke 之类的调用会把真实原因包在 MethodInvocationException、AggregateException 里，
    直接打印外层只能得到 "One or more errors occurred." 这种没有排查价值的文字。
#>
function Get-InnerMessage {
    param([Exception]$Ex)

    $cur = $Ex

    while ($cur.InnerException -ne $null) { $cur = $cur.InnerException }

    if ($cur -eq $Ex) { return "$($Ex.GetType().Name): $($Ex.Message)" }

    return "$($cur.GetType().Name): $($cur.Message)"
}

# ---------------------------------------------------------------- 测试一个代理
<#
    四步全过才算可用：
      1) 连上代理端口
      2) 发 CONNECT 隧道请求并拿到 200
      3) 在隧道里完成 TLS 握手   <-- 关键：有的代理隧道能建起来但 TLS 会挂
      4) 发真实 HTTPS 请求并读到 genres
#>
function Test-ProxyForSteam {
    param([string]$ProxyAddress, [int]$TimeoutMs = 12000)

    # 特殊值：不使用代理，直连 Steam
    if ($ProxyAddress -eq 'DIRECT') {
        return Test-DirectForSteam -TimeoutMs $TimeoutMs
    }

    $raw = $ProxyAddress -replace '^\w+://', ''
    $parts = $raw.Split(':')

    if ($parts.Count -lt 2) { return @{ Ok = $false; Step = '地址格式'; Detail = "缺少端口: $ProxyAddress" } }

    $phost = $parts[0]
    $pport = 0
    if (-not [int]::TryParse($parts[1], [ref]$pport)) {
        return @{ Ok = $false; Step = '地址格式'; Detail = "端口无效: $ProxyAddress" }
    }

    $client = $null
    $ssl = $null
    $stream = $null

    try {
        # 1) 连代理端口
        $client = New-Object System.Net.Sockets.TcpClient
        $connectTask = $client.ConnectAsync($phost, $pport)

        if (-not $connectTask.Wait($TimeoutMs)) {
            return @{ Ok = $false; Step = '连接代理'; Detail = "连接 $phost`:$pport 超时" }
        }
        if (-not $client.Connected) {
            return @{ Ok = $false; Step = '连接代理'; Detail = "无法连接 $phost`:$pport（端口未监听？）" }
        }

        $stream = $client.GetStream()
        $stream.ReadTimeout = $TimeoutMs
        $stream.WriteTimeout = $TimeoutMs

        # 2) CONNECT 隧道
        $req = "CONNECT $steamHost`:443 HTTP/1.1`r`nHost: $steamHost`:443`r`nProxy-Connection: keep-alive`r`n`r`n"
        $bytes = [Text.Encoding]::ASCII.GetBytes($req)
        $stream.Write($bytes, 0, $bytes.Length)
        $stream.Flush()

        $buf = New-Object byte[] 2048
        $readTask = $stream.ReadAsync($buf, 0, $buf.Length)

        if (-not $readTask.Wait($TimeoutMs)) {
            return @{ Ok = $false; Step = 'CONNECT 隧道'; Detail = '代理无响应' }
        }

        $n = $readTask.Result
        if ($n -le 0) {
            return @{ Ok = $false; Step = 'CONNECT 隧道'; Detail = '代理关闭了连接' }
        }

        $response = [Text.Encoding]::ASCII.GetString($buf, 0, $n)
        $firstLine = ($response -split "`r`n")[0]

        if ($response -notmatch '200') {
            return @{ Ok = $false; Step = 'CONNECT 隧道'; Detail = $firstLine }
        }

        # 3) 隧道内 TLS 握手
        #    这一步是区分「代理真的可用」和「隧道能建但 TLS 被破坏」的关键，
        #    所以单独 try，把真实原因如实报出来。
        try {
            $ssl = New-Object System.Net.Security.SslStream(
                $stream, $false, $script:CertCallback)

            $authTask = $ssl.AuthenticateAsClientAsync($steamHost)

            if (-not $authTask.Wait($TimeoutMs)) {
                return @{ Ok = $false; Step = 'TLS 握手'; Detail = '握手超时' }
            }
        }
        catch {
            return @{ Ok = $false; Step = 'TLS 握手'; Detail = (Get-InnerMessage $_.Exception) }
        }

        # 4) 真实请求
        $get = "GET /api/appdetails?appids=2845270&l=english HTTP/1.1`r`n" +
               "Host: $steamHost`r`n" +
               "User-Agent: Mozilla/5.0`r`n" +
               "Accept: application/json`r`n" +
               "Connection: close`r`n`r`n"

        $gb = [Text.Encoding]::ASCII.GetBytes($get)
        $ssl.Write($gb, 0, $gb.Length)
        $ssl.Flush()

        $ms = New-Object System.IO.MemoryStream
        $buf2 = New-Object byte[] 16384

        while ($ms.Length -lt 262144) {
            $rt = $ssl.ReadAsync($buf2, 0, $buf2.Length)
            if (-not $rt.Wait($TimeoutMs)) { break }
            if ($rt.Result -le 0) { break }
            $ms.Write($buf2, 0, $rt.Result)
        }

        $text = [Text.Encoding]::UTF8.GetString($ms.ToArray())
        $ms.Dispose()

        if ($text.Length -eq 0) {
            return @{ Ok = $false; Step = '读取响应'; Detail = '握手成功但没读到数据' }
        }

        $statusLine = ($text -split "`n")[0].Trim()

        if ($text -notmatch '"genres"') {
            return @{ Ok = $false; Step = '响应内容'; Detail = "有响应但没有 genres（$statusLine）" }
        }

        return @{ Ok = $true; Step = '全部通过'; Detail = "TLS + 响应正常（$statusLine）" }
    }
    catch {
        # Wait() 抛的是 AggregateException，必须挖出内层原因才有排查价值
        $ex = $_.Exception

        while ($ex -is [System.AggregateException] -and $ex.InnerException) { $ex = $ex.InnerException }

        return @{ Ok = $false; Step = '异常'; Detail = "$($ex.GetType().Name): $($ex.Message)" }
    }
    finally {
        if ($ssl)    { try { $ssl.Dispose() }    catch { } }
        if ($stream) { try { $stream.Dispose() } catch { } }
        if ($client) { try { $client.Close() }   catch { } }
    }
}

# ---------------------------------------------------------------- 端口在监听？
function Test-PortOpen {
    param([string]$Addr, [int]$TimeoutMs = 1000)

    $raw = $Addr -replace '^\w+://', ''
    $parts = $raw.Split(':')

    if ($parts.Count -lt 2) { return $false }

    $port = 0
    if (-not [int]::TryParse($parts[1], [ref]$port)) { return $false }

    $tcp = New-Object System.Net.Sockets.TcpClient

    try {
        $task = $tcp.ConnectAsync($parts[0], $port)
        return ($task.Wait($TimeoutMs) -and $tcp.Connected)
    }
    catch { return $false }
    finally { try { $tcp.Close() } catch { } }
}

# ---------------------------------------------------------------- 发现端口
function Get-CandidateList {
    # 注意：这里必须用普通 [hashtable]（@{}）。
    # 用 [ordered]@{} 会得到 OrderedDictionary，把它传给参数声明为 [hashtable] 的函数
    # 会**静默绑定失败**（AddOne 根本没被调用），曾经因此导致候选列表恒为空。
    $map = @{}

    # 1) 注册表系统代理（Clash 的「系统代理」开关写这里）
    try {
        $reg = Get-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Internet Settings' -ErrorAction SilentlyContinue

        if ($reg -and $reg.ProxyEnable -eq 1 -and $reg.ProxyServer) {
            $v = $reg.ProxyServer.Trim()
            $k = ($v -replace '^\w+://', '').ToLowerInvariant()

            if (-not $map.ContainsKey($k)) {
                $map[$k] = @{ Address = $v; Source = '系统代理设置（注册表）' }
            }
        }
    }
    catch { }

    # 2) Clash 配置目录里的端口定义
    $dirs = @(
        (Join-Path $env:APPDATA 'io.github.clash-verge-rev.clash-verge-rev'),
        (Join-Path $env:LOCALAPPDATA 'io.github.clash-verge-rev.clash-verge-rev'),
        (Join-Path $env:APPDATA 'clash-verge'),
        (Join-Path $env:APPDATA 'Clash Verge'),
        (Join-Path $env:APPDATA 'clash-nyanpasu'),
        (Join-Path $env:USERPROFILE '.config\clash'),
        (Join-Path $env:USERPROFILE '.config\mihomo')
    )

    foreach ($d in $dirs) {
        if (-not (Test-Path $d)) { continue }

        foreach ($file in @('config.yaml', 'clash-verge.yaml', 'verge.yaml')) {
            $p = Join-Path $d $file
            if (-not (Test-Path $p)) { continue }

            $lines = @()
            try { $lines = @(Get-Content $p -Encoding UTF8 -ErrorAction SilentlyContinue) } catch { continue }

            foreach ($line in $lines) {
                $num = $null
                $label = $null

                # 只匹配定义端口的行，避开节点里的 "port: 18662"
                if ($line -match '^\s*(mixed-port|socks-port|port)\s*:\s*(\d{2,5})') {
                    $label = "$file 的 $($Matches[1])"
                    $num = $Matches[2]
                }
                elseif ($line -match '^\s*(verge_\w*port)\s*:\s*(\d{2,5})') {
                    $label = "$file 的 $($Matches[1])"
                    $num = $Matches[2]
                }

                if ($num) {
                    $k = "127.0.0.1:$num"

                    if (-not $map.ContainsKey($k)) {
                        $map[$k] = @{ Address = "http://$k"; Source = $label }
                    }
                }
            }
        }
    }

    # 3) 常见默认端口
    foreach ($p in @(7897, 7890, 7891, 7898, 7899, 1080, 10808, 10809, 2080)) {
        $k = "127.0.0.1:$p"

        if (-not $map.ContainsKey($k)) {
            $map[$k] = @{ Address = "http://$k"; Source = '常见默认端口' }
        }
    }

    # 过滤：只保留端口真的在监听的
    $out = New-Object System.Collections.Generic.List[object]

    foreach ($k in $map.Keys) {
        $it = $map[$k]

        if (Test-PortOpen -Addr $it.Address) {
            $out.Add([pscustomobject]@{ Address = $it.Address; Source = $it.Source })
        }
    }

    # 最后补上「不使用代理（直连）」：
    # 实测过「代理反而破坏 TLS、直连却是好的」这种情况，所以直连也要一起测。
    $out.Add([pscustomobject]@{ Address = 'DIRECT'; Source = '不使用代理（直连）' })

    return $out
}

# ---------------------------------------------------------------- 测试直连
<#
    不用代理，直接连 Steam 做 TLS + HTTP。
    用于判断「是不是代理的问题」：直连通而代理不通 → 该把程序设成 Direct。
#>
function Test-DirectForSteam {
    param([int]$TimeoutMs = 12000)

    $client = $null
    $ssl = $null
    $stream = $null

    try {
        # 1) 直连 TCP 443
        $client = New-Object System.Net.Sockets.TcpClient
        $connectTask = $client.ConnectAsync($steamHost, 443)

        if (-not $connectTask.Wait($TimeoutMs)) {
            return @{ Ok = $false; Step = '直连 TCP'; Detail = "连接 $steamHost`:443 超时" }
        }
        if (-not $client.Connected) {
            return @{ Ok = $false; Step = '直连 TCP'; Detail = "无法连接 $steamHost`:443" }
        }

        $stream = $client.GetStream()
        $stream.ReadTimeout = $TimeoutMs
        $stream.WriteTimeout = $TimeoutMs

        # 2) TLS 握手
        try {
            $ssl = New-Object System.Net.Security.SslStream(
                $stream, $false, $script:CertCallback)

            $authTask = $ssl.AuthenticateAsClientAsync($steamHost)

            if (-not $authTask.Wait($TimeoutMs)) {
                return @{ Ok = $false; Step = '直连 TLS'; Detail = '握手超时' }
            }
        }
        catch {
            return @{ Ok = $false; Step = '直连 TLS'; Detail = (Get-InnerMessage $_.Exception) }
        }

        # 3) 真实请求
        $get = "GET /api/appdetails?appids=2845270&l=english HTTP/1.1`r`n" +
               "Host: $steamHost`r`n" +
               "User-Agent: Mozilla/5.0`r`n" +
               "Accept: application/json`r`n" +
               "Connection: close`r`n`r`n"

        $gb = [Text.Encoding]::ASCII.GetBytes($get)
        $ssl.Write($gb, 0, $gb.Length)
        $ssl.Flush()

        $ms = New-Object System.IO.MemoryStream
        $buf = New-Object byte[] 16384

        while ($ms.Length -lt 262144) {
            $rt = $ssl.ReadAsync($buf, 0, $buf.Length)
            if (-not $rt.Wait($TimeoutMs)) { break }
            if ($rt.Result -le 0) { break }
            $ms.Write($buf, 0, $rt.Result)
        }

        $text = [Text.Encoding]::UTF8.GetString($ms.ToArray())
        $ms.Dispose()

        if ($text.Length -eq 0) {
            return @{ Ok = $false; Step = '直连读取'; Detail = '握手成功但没读到数据' }
        }

        $statusLine = ($text -split "`n")[0].Trim()

        if ($text -notmatch '"genres"') {
            return @{ Ok = $false; Step = '直连响应'; Detail = "有响应但没有 genres（$statusLine）" }
        }

        return @{ Ok = $true; Step = '全部通过'; Detail = "直连 TLS + 响应正常（$statusLine）" }
    }
    catch {
        return @{ Ok = $false; Step = '直连异常'; Detail = (Get-InnerMessage $_.Exception) }
    }
    finally {
        if ($ssl)    { try { $ssl.Dispose() }    catch { } }
        if ($stream) { try { $stream.Dispose() } catch { } }
        if ($client) { try { $client.Close() }   catch { } }
    }
}

# ---------------------------------------------------------------- 主流程
Write-Host ""
Write-Host "游戏补丁安装器 - Clash 代理自动发现" -ForegroundColor White
Write-Host ("=" * 62)

Write-Head "发现候选代理地址"

$candidates = @()

if ($Address) {
    $a = $Address.Trim()
    if ($a -notmatch '^https?://') { $a = "http://$a" }

    $candidates = @([pscustomobject]@{ Address = $a; Source = '手动指定（-Address）' })
    Write-Info "使用手动指定地址：$a"
}
else {
    $candidates = @(Get-CandidateList)

    if ($candidates.Count -eq 0) {
        Write-Bad "没有发现任何正在监听的候选端口。"
        Write-Info "请确认 Clash / Clash Verge 正在运行；或用 -Address 手动指定，例如："
        Write-Info "  -Address 127.0.0.1:7897"
    }
    else {
        foreach ($c in $candidates) {
            Write-Info "$($c.Address)    <- $($c.Source)"
        }
    }
}

# ---------------------------------------------------------------- 逐个测试
if ($candidates.Count -gt 0) {
    Write-Head "测试每个地址能否真正访问 Steam（含 TLS 握手）"

    foreach ($c in $candidates) {
        Write-Host "    $($c.Address) ... " -NoNewline

        $r = Test-ProxyForSteam -ProxyAddress $c.Address

        if ($r.Ok) {
            Write-Host "可用" -ForegroundColor Green
            Write-Ok $r.Detail
        }
        else {
            Write-Host "不可用" -ForegroundColor Red
            Write-Bad "$($r.Step)：$($r.Detail)"
        }

        $results.Add([pscustomobject]@{
            Address = $c.Address
            Source  = $c.Source
            Ok      = [bool]$r.Ok
            Step    = $r.Step
            Detail  = $r.Detail
        })
    }
}

# ---------------------------------------------------------------- 结论
$best = $results | Where-Object { $_.Ok } | Select-Object -First 1

Write-Head "结论"

if ($best) {
    Write-Ok "可用代理：$($best.Address)"
}
elseif ($results.Count -gt 0) {
    Write-Bad "所有候选地址都无法访问 Steam。"
    Write-Info "含义：Clash 在运行、端口也通，但经它访问 Steam 失败。"
    Write-Info "常见原因：Clash 里没有可用节点，或规则把 Steam 走了 DIRECT 而本机直连不通。"
    Write-Info "建议：在 Clash 里换个节点重跑本脚本；或在程序里把联网方式设为 Direct 试试。"
}

# ---------------------------------------------------------------- 写入配置
$configPath = Join-Path $env:APPDATA 'GalPatchManager\config.json'

if ($NoApply) {
    Write-Info "（-NoApply 已指定，不修改配置文件）"
}
elseif (-not $best) {
    Write-Info "没有可用代理，未修改配置文件。"
}
elseif (-not (Test-Path $configPath)) {
    Write-Bad "找不到配置文件：$configPath"
    Write-Info "请先运行一次「游戏补丁安装器」生成配置，然后再跑本脚本。"
}
else {
    Write-Head "写入配置"

    $backup = "$configPath.bak-" + (Get-Date -Format 'yyyyMMdd-HHmmss')
    Copy-Item $configPath $backup -Force
    Write-Info "已备份：$backup"

    $json = Get-Content $configPath -Raw -Encoding UTF8 | ConvertFrom-Json

    if ($best.Address -eq 'DIRECT') {
        # 直连可用：不填代理，NetworkMode 设为 Direct
        $json.NetworkMode = 'Direct'
        $json.CustomProxyUrl = ''
    }
    else {
        $json.CustomProxyUrl = $best.Address
        $json.NetworkMode = 'CustomProxy'
    }

    if ($json.PSObject.Properties.Name -contains 'EnableSteamMetadata') {
        $json.EnableSteamMetadata = $true
    }
    else {
        $json | Add-Member -NotePropertyName EnableSteamMetadata -NotePropertyValue $true -Force
    }

    $out = $json | ConvertTo-Json -Depth 20
    [IO.File]::WriteAllText($configPath, $out, (New-Object Text.UTF8Encoding($false)))

    if ($best.Address -eq 'DIRECT') {
        Write-Ok "NetworkMode    = Direct（不使用代理）"
        Write-Ok "CustomProxyUrl = （已清空）"
    }
    else {
        Write-Ok "NetworkMode    = CustomProxy"
        Write-Ok "CustomProxyUrl = $($best.Address)"
    }

    # 旧备份只留最近 5 个
    Get-ChildItem "$configPath.bak-*" -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime -Descending |
        Select-Object -Skip 5 |
        Remove-Item -Force -ErrorAction SilentlyContinue
}

# ---------------------------------------------------------------- 启动程序
if ($Launch) {
    Write-Head "启动程序"

    if (-not $App) {
        $scriptDir = if ($PSScriptRoot) { $PSScriptRoot } else { (Get-Location).Path }
        $parentDir = Split-Path $scriptDir -Parent

        $tryPaths = @(
            (Join-Path $scriptDir 'GalPatchManager.exe'),
            (Join-Path $scriptDir 'dist\GalPatchManager-v1.0.0-win-x64\GalPatchManager.exe'),
            (Join-Path $parentDir 'dist\GalPatchManager-v1.0.0-win-x64\GalPatchManager.exe'),
            (Join-Path $scriptDir 'src\GalPatchManager\bin\Release\net8.0-windows\win-x64\GalPatchManager.exe')
        )

        $App = $tryPaths | Where-Object { Test-Path $_ } | Select-Object -First 1
    }

    if ($App -and (Test-Path $App)) {
        Start-Process -FilePath $App | Out-Null
        Write-Ok "已启动：$App"
    }
    else {
        Write-Bad "找不到 GalPatchManager.exe，请用 -App 指定路径。"
    }
}

# ---------------------------------------------------------------- 收尾
Write-Host ""
Write-Host ("=" * 62) -ForegroundColor White

if ($best) {
    Write-Host "下一步：" -ForegroundColor White
    Write-Host "  1. 打开「游戏补丁安装器」→ 设置 → 游戏归类" -ForegroundColor Gray
    Write-Host "  2. 点「测试连接」确认（应显示：连接成功）" -ForegroundColor Gray
    Write-Host "  3. 点「立即重新归类」，类型菜单就会填充" -ForegroundColor Gray
}
else {
    Write-Host "没有可写入的代理。" -ForegroundColor Yellow
    Write-Host "可在「设置 → 游戏归类 → 联网方式」里逐个试 Direct / SystemProxy。" -ForegroundColor Gray
}

Write-Host ""
Write-Host "（本脚本只改动了本程序的 config.json；未修改系统代理，也未改动 Clash 配置）" -ForegroundColor DarkGray
Write-Host ""

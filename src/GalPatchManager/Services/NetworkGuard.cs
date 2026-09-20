using System.Net;
using System.Net.Sockets;

namespace GalPatchManager.Services;

/// <summary>
/// 网络地址安全检查。
/// 自定义搜索接口可能被恶意配置指向内网，这里做一层 SSRF 防护：
/// 拒绝解析到环回 / 私有 / 链路本地 / 组播地址的目标（SearXNG 场景可显式放行环回）。
/// </summary>
public static class NetworkGuard
{
    public static async Task<bool> IsPublicEndpointAsync(
        Uri uri,
        bool allowLoopback,
        CancellationToken cancellationToken = default)
    {
        if (uri is null) return false;

        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) return false;

        // 主机名本身就是 IP 时先直接判断
        if (IPAddress.TryParse(uri.Host, out var literal))
        {
            return IsAllowedAddress(literal, allowLoopback);
        }

        try
        {
            var addresses = await Dns.GetHostAddressesAsync(uri.Host, cancellationToken)
                .ConfigureAwait(false);

            if (addresses.Length == 0) return false;

            // 只要有一个地址不安全就拒绝，避免 DNS 多记录绕过
            return addresses.All(a => IsAllowedAddress(a, allowLoopback));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Warn($"域名解析失败 {uri.Host}: {ex.Message}");
            return false;
        }
    }

    private static bool IsAllowedAddress(IPAddress address, bool allowLoopback)
    {
        if (IPAddress.IsLoopback(address)) return allowLoopback;

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = address.GetAddressBytes();

            // 0.0.0.0/8
            if (b[0] == 0) return false;

            // 10.0.0.0/8
            if (b[0] == 10) return false;

            // 172.16.0.0/12
            if (b[0] == 172 && b[1] >= 16 && b[1] <= 31) return false;

            // 192.168.0.0/16
            if (b[0] == 192 && b[1] == 168) return false;

            // 169.254.0.0/16 链路本地
            if (b[0] == 169 && b[1] == 254) return false;

            // 100.64.0.0/10 运营商级 NAT
            if (b[0] == 100 && b[1] >= 64 && b[1] <= 127) return false;

            // 224.0.0.0/4 组播, 240.0.0.0/4 保留
            if (b[0] >= 224) return false;

            return true;
        }

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || address.IsIPv6Multicast)
                return false;

            // ::1 已由 IsLoopback 处理；IPv4 映射地址再判一次
            if (address.IsIPv4MappedToIPv6)
            {
                return IsAllowedAddress(address.MapToIPv4(), allowLoopback);
            }

            // 唯一本地地址 fc00::/7
            var bytes = address.GetAddressBytes();
            if ((bytes[0] & 0xFE) == 0xFC) return false;

            return true;
        }

        return false;
    }
}

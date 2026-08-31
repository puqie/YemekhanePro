using System.Net;
using System.Net.Sockets;

namespace Yemekhane.Application.Common;

public static class OutboundEndpointPolicy
{
    public static Uri ValidateSyntax(string? value, bool allowHttp = false, bool allowPrivateNetworks = false)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttps && (!allowHttp || uri.Scheme != Uri.UriSchemeHttp)) ||
            !string.IsNullOrEmpty(uri.UserInfo))
            throw new RequestValidationException("Dış servis endpoint'i geçerli, kimlik bilgisi içermeyen bir HTTPS adresi olmalıdır.");

        if (!allowPrivateNetworks && IPAddress.TryParse(uri.Host, out var address) && IsPrivate(address))
            throw new RequestValidationException("Dış servis endpoint'i özel veya yerel ağ adresi olamaz.");
        return uri;
    }

    public static async Task<Uri> ValidateAsync(string? value, bool allowHttp = false,
        bool allowPrivateNetworks = false, CancellationToken cancellationToken = default)
    {
        var uri = ValidateSyntax(value, allowHttp, allowPrivateNetworks);
        if (allowPrivateNetworks || IPAddress.TryParse(uri.Host, out _)) return uri;

        IPAddress[] addresses;
        try { addresses = await Dns.GetHostAddressesAsync(uri.DnsSafeHost, cancellationToken); }
        catch (SocketException) { throw new RequestValidationException("Dış servis endpoint'i çözümlenemedi."); }
        if (addresses.Length == 0 || addresses.Any(IsPrivate))
            throw new RequestValidationException("Dış servis endpoint'i özel veya yerel ağa çözümlenemez.");
        return uri;
    }

    private static bool IsPrivate(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any) ||
            address.Equals(IPAddress.None) || address.Equals(IPAddress.IPv6None) || address.IsIPv6LinkLocal ||
            address.IsIPv6SiteLocal || (address.AddressFamily == AddressFamily.InterNetworkV6 &&
                (address.GetAddressBytes()[0] & 0xfe) == 0xfc)) return true;
        if (address.AddressFamily != AddressFamily.InterNetwork) return false;
        var bytes = address.GetAddressBytes();
        return bytes[0] is 0 or 10 or 127 ||
               bytes[0] == 169 && bytes[1] == 254 ||
               bytes[0] == 172 && bytes[1] is >= 16 and <= 31 ||
               bytes[0] == 192 && bytes[1] == 168 ||
               bytes[0] >= 224;
    }
}

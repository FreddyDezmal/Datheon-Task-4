using System.Net;
using System.Net.Sockets;

namespace Storefront.Api.Security;

/// <summary>
/// Which "client" a request counts against for rate limits and sign-in protection (Security-Audit.md, SA-03).
///
/// An IPv4 address is one client. An IPv6 client is its /64 network: a single ordinary connection is
/// usually given a whole /64 (about 1.8 × 10¹⁹ addresses), so counting each address separately let one
/// client rotate through fresh limits indefinitely. IPv4-mapped IPv6 addresses count as the IPv4 address.
/// </summary>
public static class ClientPartition
{
    public const string Unknown = "unknown";

    public static string For(IPAddress? address)
    {
        if (address is null)
        {
            return Unknown;
        }

        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (address.AddressFamily != AddressFamily.InterNetworkV6)
        {
            return address.ToString();
        }

        var bytes = address.GetAddressBytes();
        Array.Clear(bytes, 8, 8);
        return new IPAddress(bytes) + "/64";
    }

    public static string For(HttpContext context) => For(context.Connection.RemoteIpAddress);
}

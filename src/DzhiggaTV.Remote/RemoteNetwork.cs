using L = DzhiggaTV.Localization.Texts;
using System.Collections;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace DzhiggaTV.Remote;

public sealed record RemoteAddress(string Address, int Prefix, string Adapter, bool Private, bool Virtual)
{
    public override string ToString() => $"{Address} · {Adapter}{(Private ? "" : L.T(" · публичная / неизвестная сеть"))}";
    public bool Contains(IPAddress address)
    {
        var a = IPAddress.Parse(Address).GetAddressBytes(); var b = address.MapToIPv4().GetAddressBytes();
        if (b.Length != a.Length) return false;
        for (int i = 0; i < 4; i++) { int bits = Math.Clamp(Prefix - i * 8, 0, 8); int mask = (255 << (8 - bits)) & 255; if ((a[i] & mask) != (b[i] & mask)) return false; }
        return true;
    }
}

public static class RemoteNetwork
{
    public static IReadOnlyList<RemoteAddress> Addresses()
    {
        var trusted = PrivateAdapters();
        return NetworkInterface.GetAllNetworkInterfaces().Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
            .SelectMany(n => n.GetIPProperties().UnicastAddresses.Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork && IsLan(a.Address))
                .Select(a => new RemoteAddress(a.Address.ToString(), a.PrefixLength, n.Name, trusted.Contains(n.Id.Trim('{', '}')),
                    n.NetworkInterfaceType == NetworkInterfaceType.Tunnel || new[] { "vpn", "virtual", "hyper-v", "tap", "tailscale", "wireguard", "xray", "happ", "wintun", "clash", "sing-box", "vmware" }.Any(s => (n.Description + n.Name).Contains(s, StringComparison.OrdinalIgnoreCase)))))
            .OrderByDescending(a => a.Private).ThenBy(a => a.Virtual).ToArray();
    }
    public static RemoteAddress Select(string preferred)
    {
        var addresses = Addresses();
        var selected = preferred.Length > 0 ? addresses.FirstOrDefault(a => a.Address == preferred) : addresses.FirstOrDefault(a => a.Private && !a.Virtual) ?? addresses.FirstOrDefault(a => a.Private);
        if (selected is null) throw new RemoteException(L.T("Не найдена частная домашняя сеть. Подключите Ethernet или Wi-Fi и выберите профиль «Частная сеть» в Windows."));
        if (!selected.Private) throw new RemoteException(L.T("Remote доступен только в частной сети Windows. Проверьте профиль выбранного адаптера."));
        return selected;
    }
    public static bool IsLan(IPAddress address)
    {
        var b = address.MapToIPv4().GetAddressBytes();
        return b.Length == 4 && (b[0] == 10 || b[0] == 192 && b[1] == 168 || b[0] == 172 && b[1] is >= 16 and <= 31);
    }
    private static HashSet<string> PrivateAdapters()
    {
        // NLM's automation enumerator is unreliable on the WPF STA dispatcher.
        // Keep all COM objects and enumeration in one MTA apartment.
        return Thread.CurrentThread.GetApartmentState() == ApartmentState.STA
            ? Task.Run(PrivateAdaptersCore).GetAwaiter().GetResult() : PrivateAdaptersCore();
    }
    private static HashSet<string> PrivateAdaptersCore()
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!OperatingSystem.IsWindows()) return result;
        try
        {
            return WindowsNetworkProfiles.PrivateAdapters();
        }
        catch (Exception e) when (e is COMException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException or InvalidCastException or ArgumentException) { /* Unknown profile is never treated as private. */ }
        return result;
    }
}

/// <summary>Validate and pin every DNS result at connection time, including redirects. No environment HTTP proxy.</summary>
public sealed class SourcePolicy
{
    private readonly bool _allowLoopback;
    public SourcePolicy(bool allowLoopbackForTests = false) => _allowLoopback = allowLoopbackForTests;
    public void Validate(Uri uri)
    {
        if (!uri.IsAbsoluteUri || uri.Scheme is not ("http" or "https") || uri.UserInfo.Length > 0 || uri.Fragment.Length > 0 || uri.Host.Length == 0)
            throw new RemoteException(L.T("Этот адрес потока нельзя передать в Remote."));
        if (IPAddress.TryParse(uri.Host.Trim('[', ']'), out var ip) && !Allowed(ip)) throw new RemoteException(L.T("Remote не обращается к локальным и служебным адресам источников."));
    }
    public bool Allowed(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (_allowLoopback && IPAddress.IsLoopback(address)) return true;
        if (IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any)) return false;
        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            var b = address.GetAddressBytes();
            return !address.IsIPv6LinkLocal && !address.IsIPv6SiteLocal && !address.IsIPv6Multicast && (b[0] & 0xfe) != 0xfc && (b[0] & 0xe0) == 0x20;
        }
        var v = address.GetAddressBytes();
        return v[0] is not (0 or 10 or 127) && v[0] < 224 && !(v[0] == 100 && v[1] is >= 64 and <= 127) &&
            !(v[0] == 169 && v[1] == 254) && !(v[0] == 172 && v[1] is >= 16 and <= 31) && !(v[0] == 192 && v[1] == 168) &&
            !(v[0] == 192 && v[1] == 0) && !(v[0] == 198 && v[1] is 18 or 19);
    }
    public HttpClient CreateClient()
    {
        var handler = new SocketsHttpHandler { AllowAutoRedirect = false, UseProxy = false, UseCookies = false, ConnectTimeout = TimeSpan.FromSeconds(10), PooledConnectionLifetime = TimeSpan.FromMinutes(2), AutomaticDecompression = DecompressionMethods.All };
        handler.ConnectCallback = async (context, token) =>
        {
            var addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, token);
            if (addresses.Length == 0 || addresses.Any(a => !Allowed(a))) throw new RemoteException(L.T("Источник перенаправляет запрос в локальную или служебную сеть."), 502);
            foreach (var address in addresses)
            {
                var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                try { await socket.ConnectAsync(address, context.DnsEndPoint.Port, token); return new NetworkStream(socket, true); }
                catch (SocketException) { socket.Dispose(); }
                catch { socket.Dispose(); throw; }
            }
            throw new RemoteException(L.T("Не удалось подключиться к IPTV-провайдеру."), 502);
        };
        return new(handler) { Timeout = Timeout.InfiniteTimeSpan };
    }
}

using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;

namespace Lumen.Core;

public sealed record YeelightDeviceInfo(
    string Id,
    string Host,
    int Port,
    string Model,
    string Name,
    string FirmwareVersion,
    IReadOnlySet<string> Support)
{
    public string DisplayName => string.IsNullOrWhiteSpace(Name) ? Model : Name;
}

/// <summary>
/// Поиск ламп по SSDP-подобному протоколу Yeelight (multicast 239.255.255.250:1982).
/// Запрос отправляется отдельно с КАЖДОГО сетевого интерфейса — иначе Windows может
/// отправить multicast в адаптер VMware/VPN, и лампа запрос не увидит.
/// </summary>
public static class YeelightDiscovery
{
    private static readonly IPEndPoint MulticastEndpoint = new(IPAddress.Parse("239.255.255.250"), 1982);

    private const string SearchMessage =
        "M-SEARCH * HTTP/1.1\r\n" +
        "HOST: 239.255.255.250:1982\r\n" +
        "MAN: \"ssdp:discover\"\r\n" +
        "ST: wifi_bulb\r\n";

    public static async Task<IReadOnlyList<YeelightDeviceInfo>> DiscoverAsync(
        TimeSpan timeout, CancellationToken ct = default)
    {
        var found = new Dictionary<string, YeelightDeviceInfo>();
        var gate = new object();

        var tasks = GetLocalIPv4Addresses()
            .Select(addr => SearchOnInterfaceAsync(addr, MulticastEndpoint, timeout, info =>
            {
                lock (gate) found[info.Id] = info;
            }, ct))
            .ToList();

        await Task.WhenAll(tasks).ConfigureAwait(false);
        lock (gate) return found.Values.OrderBy(d => d.Host).ToList();
    }

    /// <summary>Тот же запрос, но напрямую на IP (если multicast в сети режется).</summary>
    public static async Task<YeelightDeviceInfo?> ProbeAsync(string host, TimeSpan timeout, CancellationToken ct = default)
    {
        YeelightDeviceInfo? result = null;
        var target = new IPEndPoint(IPAddress.Parse(host), 1982);
        await SearchOnInterfaceAsync(IPAddress.Any, target, timeout, info => result ??= info, ct).ConfigureAwait(false);
        return result;
    }

    private static async Task SearchOnInterfaceAsync(
        IPAddress local, IPEndPoint target, TimeSpan timeout,
        Action<YeelightDeviceInfo> onFound, CancellationToken ct)
    {
        using var udp = new UdpClient(AddressFamily.InterNetwork);
        try
        {
            udp.Client.Bind(new IPEndPoint(local, 0));
            if (!local.Equals(IPAddress.Any))
            {
                // Явно указываем, через какой интерфейс уходит multicast.
                udp.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface,
                    local.GetAddressBytes());
            }
            udp.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 2);

            var payload = Encoding.ASCII.GetBytes(SearchMessage);
            // UDP теряется — шлём пару раз.
            for (var i = 0; i < 2; i++)
                await udp.SendAsync(payload, target, ct).ConfigureAwait(false);
        }
        catch (SocketException)
        {
            return; // интерфейс без маршрута — пропускаем
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);
        try
        {
            while (true)
            {
                var packet = await udp.ReceiveAsync(timeoutCts.Token).ConfigureAwait(false);
                var info = Parse(Encoding.ASCII.GetString(packet.Buffer));
                if (info != null) onFound(info);
            }
        }
        catch (OperationCanceledException) { }
        catch (SocketException) { }
    }

    internal static YeelightDeviceInfo? Parse(string response)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rawLine in response.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            var colon = line.IndexOf(':');
            if (colon <= 0) continue;
            headers[line[..colon].Trim()] = line[(colon + 1)..].Trim();
        }

        // Location: yeelight://192.168.10.250:55443
        if (!headers.TryGetValue("Location", out var location)
            || !Uri.TryCreate(location, UriKind.Absolute, out var uri)
            || uri.Scheme != "yeelight")
            return null;

        headers.TryGetValue("id", out var id);
        headers.TryGetValue("model", out var model);
        headers.TryGetValue("name", out var name);
        headers.TryGetValue("fw_ver", out var fw);
        headers.TryGetValue("support", out var support);

        return new YeelightDeviceInfo(
            Id: string.IsNullOrEmpty(id) ? uri.Host : id,
            Host: uri.Host,
            Port: uri.Port > 0 ? uri.Port : YeelightClient.DefaultPort,
            Model: model ?? "yeelight",
            Name: name ?? "",
            FirmwareVersion: fw ?? "",
            Support: (support ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet());
    }

    private static IEnumerable<IPAddress> GetLocalIPv4Addresses()
    {
        var result = new List<IPAddress>();
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up) continue;
            if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;

            foreach (var ua in nic.GetIPProperties().UnicastAddresses)
                if (ua.Address.AddressFamily == AddressFamily.InterNetwork)
                    result.Add(ua.Address);
        }
        if (result.Count == 0) result.Add(IPAddress.Any);
        return result;
    }
}

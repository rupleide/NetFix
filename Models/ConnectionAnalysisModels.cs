using System.Windows.Media;

namespace NetFix.Models;

public class ProcessItemModel
{
    public required string AppName { get; init; }
    public required string DisplayName { get; init; }
    public required string ExePath { get; init; }
    public string AppKey { get; init; } = "";
    public ImageSource? Icon { get; init; }
    public List<int> ProcessIds { get; init; } = [];
    public int MainProcessId => ProcessIds.Count > 0 ? ProcessIds[0] : 0;
    public int ProcessCount => ProcessIds.Count;
    public string ProcessCountBadge => ProcessCount > 1 ? $"{ProcessCount} процесса" : $"PID: {MainProcessId}";
    public string WindowTitle { get; init; } = "";
    public int ConnectionCount { get; set; }
    public bool IsCommonApp { get; init; }
    public bool HasWindow { get; init; }
}

public record LayerDnsInfo
{
    public string Domain { get; init; } = "";
    public string Source { get; init; } = "DNS / IP";
    public bool IsHosts { get; init; }
    public string Details { get; init; } = "";
}

public record LayerRoutingInfo
{
    public string AdapterName { get; init; } = "";
    public string AdapterDescription { get; init; } = "";
    public string AdapterType { get; init; } = "Физический";
    public bool IsVpn { get; init; }
    public uint InterfaceIndex { get; init; }
    public string Gateway { get; init; } = "";
    public string Details { get; init; } = "";
}

public record LayerPacketFilterInfo
{
    public bool IsZapretActive { get; init; }
    public string ServiceStatus { get; init; } = "";
    public string ConfigName { get; init; } = "";
    public string MatchedRule { get; init; } = "";
    public string Details { get; init; } = "";
}

public record LayerProxyInfo
{
    public bool HasProxy { get; init; }
    public string ProxyName { get; init; } = "Прямое соединение";
    public int ProxyPort { get; init; }
    public string Details { get; init; } = "";
}

public class ConnectionDetailModel
{
    public string Protocol { get; set; } = "TCP";
    public string LocalAddress { get; set; } = "";
    public int LocalPort { get; set; }
    public string RemoteAddress { get; set; } = "";
    public int RemotePort { get; set; }
    public string State { get; set; } = "";
    public int ProcessId { get; set; }
    public string ProcessName { get; set; } = "";
    public string? ExecutablePath { get; set; }
    public bool IsMainProcessOfGroup { get; set; }
    public bool IsSecondaryProcess { get; set; }
    public LayerDnsInfo Dns { get; set; } = new();
    public LayerRoutingInfo Routing { get; set; } = new();
    public LayerPacketFilterInfo PacketFilter { get; set; } = new();
    public LayerProxyInfo Proxy { get; set; } = new();

    public string RemoteDisplay => string.IsNullOrEmpty(Dns.Domain)
        ? $"{RemoteAddress}:{RemotePort}"
        : $"{Dns.Domain} ({RemoteAddress}:{RemotePort})";

    public string RttDisplay => "—";

    public string PrimaryRoute => Routing.IsVpn ? "VPN" : (IsLoopback ? "Loopback" : "Прямой");
    public List<string> RouteModifiers
    {
        get
        {
            var list = new List<string>();
            if (PacketFilter.IsZapretActive) list.Add("Zapret");
            if (Proxy.HasProxy) list.Add("TgWsProxy");
            if (Dns.IsHosts) list.Add("Hosts");
            return list;
        }
    }

    public bool IsExpanded { get; set; }

    public bool IsPrimary { get; set; }
    public ulong TotalBytesIn { get; set; }
    public ulong TotalBytesOut { get; set; }
    public ulong TotalBytes => TotalBytesIn + TotalBytesOut;
    public double BytesPerSec { get; set; }
    public double DeltaBytes { get; set; }

    public string SpeedDisplay => BytesPerSec switch
    {
        >= 1024 * 1024 * 1024 => $"{BytesPerSec / (1024.0 * 1024 * 1024):F1} ГБ/с",
        >= 1024 * 1024 => $"{BytesPerSec / (1024.0 * 1024):F1} МБ/с",
        >= 1024 => $"{BytesPerSec / 1024.0:F0} КБ/с",
        >= 50 => $"{BytesPerSec:F0} Б/с",
        _ => ""
    };

    public string TotalTrafficDisplay => TotalBytes switch
    {
        >= 1024 * 1024 * 1024 => $"{TotalBytes / (1024.0 * 1024 * 1024):F2} ГБ",
        >= 1024 * 1024 => $"{TotalBytes / (1024.0 * 1024):F1} МБ",
        >= 1024 => $"{TotalBytes / 1024.0:F1} КБ",
        > 0 => $"{TotalBytes} Б",
        _ => "0 Б"
    };

    public bool IsLoopback =>
        RemoteAddress is "127.0.0.1" or "::1" or "localhost" or "0.0.0.0" or "*" ||
        RemoteAddress.StartsWith("127.");

    public bool IsPrivateIp
    {
        get
        {
            if (IsLoopback) return true;
            if (System.Net.IPAddress.TryParse(RemoteAddress, out var ip))
            {
                byte[] b = ip.GetAddressBytes();
                if (b.Length == 4)
                {
                    if (b[0] == 10) return true;
                    if (b[0] == 172 && b[1] >= 16 && b[1] <= 31) return true;
                    if (b[0] == 192 && b[1] == 168) return true;
                    if (b[0] == 169 && b[1] == 254) return true;
                }
            }
            return false;
        }
    }
}

public record ConnectionSummaryModel
{
    public int TotalCount { get; init; }
    public int VpnCount { get; init; }
    public int DirectCount { get; init; }
    public int HostsCount { get; init; }
    public int ProxyCount { get; init; }
    public int ZapretCount { get; init; }
    public string SummaryText { get; init; } = "";
}

public record NetworkAdapterInfo
{
    public uint Index { get; init; }
    public string Name { get; init; } = "";
    public string Description { get; init; } = "";
    public string Type { get; init; } = "";
    public bool IsVpn { get; init; }
    public bool IsDefaultGateway { get; init; }
    public string IpAddresses { get; init; } = "";
    public string Gateways { get; init; } = "";
    public string DnsServers { get; init; } = "";
    public string Status { get; init; } = "";
}

public class HostsEntryModel
{
    public string Ip { get; set; } = "";
    public string Hostname { get; set; } = "";
    public bool IsNetFixManaged { get; set; }
}

public class SystemOverviewModel
{
    public List<NetworkAdapterInfo> Adapters { get; set; } = [];
    public string DefaultRouteAdapter { get; set; } = "Не определён";
    public List<string> DnsServers { get; set; } = [];
    public List<HostsEntryModel> HostsEntries { get; set; } = [];
    public int HostsCount => HostsEntries.Count;
    public bool WinDivertLoaded { get; set; }
    public string ZapretStatus { get; set; } = "Остановлен";
    public string ZapretConfig { get; set; } = "Не выбран";
    public bool TgWsProxyRunning { get; set; }
    public int TgWsProxyPort { get; set; } = 1080;
    public int TgWsProxyConnectionsCount { get; set; }
    public List<SystemProcessActivityModel> ActiveProcesses { get; set; } = [];
}

public class SystemProcessActivityModel
{
    public int ProcessId { get; set; }
    public string ProcessName { get; set; } = "";
    public string WindowTitle { get; set; } = "";
    public System.Windows.Media.ImageSource? Icon { get; set; }
    public double? CpuPercent { get; set; }
    public string CpuDisplay => CpuPercent.HasValue ? $"{CpuPercent.Value:F1}%" : "—";
    public long RamBytes { get; set; }
    public string RamDisplay => RamBytes switch
    {
        >= 1024L * 1024 * 1024 => $"{RamBytes / (1024.0 * 1024 * 1024):F1} ГБ",
        >= 1024L * 1024 => $"{RamBytes / (1024.0 * 1024):F0} МБ",
        >= 1024L => $"{RamBytes / 1024.0:F0} КБ",
        > 0 => $"{RamBytes} Б",
        _ => "—"
    };
    public double BytesPerSec { get; set; }
    public ulong TotalBytes { get; set; }
    public string SpeedDisplay => BytesPerSec switch
    {
        >= 1024 * 1024 * 1024 => $"↑↓ {BytesPerSec / (1024.0 * 1024 * 1024):F1} ГБ/с",
        >= 1024 * 1024 => $"↑↓ {BytesPerSec / (1024.0 * 1024):F1} МБ/с",
        >= 1024 => $"↑↓ {BytesPerSec / 1024.0:F0} КБ/с",
        >= 50 => $"↑↓ {BytesPerSec:F0} Б/с",
        _ => "0 Б/с"
    };
    public string TotalTrafficDisplay => TotalBytes switch
    {
        >= 1024 * 1024 * 1024 => $"{TotalBytes / (1024.0 * 1024 * 1024):F1} ГБ",
        >= 1024 * 1024 => $"{TotalBytes / (1024.0 * 1024):F1} МБ",
        >= 1024 => $"{TotalBytes / 1024.0:F0} КБ",
        > 0 => $"{TotalBytes} Б",
        _ => "0 Б"
    };
    public string NetworkActivityDisplay
    {
        get
        {
            if (BytesPerSec >= 50) return SpeedDisplay;
            if (TotalBytes > 0) return TotalTrafficDisplay;
            return "н/д";
        }
    }
    public int SocketsCount { get; set; }
    public string PrimaryRoute { get; set; } = "Прямой";
    public List<string> RouteModifiers { get; set; } = [];
}

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.NetworkInformation;
using System.Threading.Tasks;
using NetFix.Models;

namespace NetFix.Services;

public record DnsServerInfo(string Name, string Description, string Primary, string Secondary, string DohTemplate = "", bool IsCustom = false);

public static class DnsManagerService
{
    public static readonly IReadOnlyList<DnsServerInfo> PredefinedDnsServers = [
        new DnsServerInfo("Системный (DHCP)", "Использовать DNS-серверы, полученные от роутера или провайдера", "dhcp", "", ""),
        new DnsServerInfo("Xbox-DNS.ru", "Альтернативный DNS для восстановления доступа к сетевым службам Xbox Live в РФ", "111.88.96.50", "111.88.96.51", "https://xbox-dns.ru/dns-query"),
        new DnsServerInfo("Cloudflare DNS", "Высокопроизводительный публичный DNS-сервер с упором на скорость и приватность", "1.1.1.1", "1.0.0.1", "https://cloudflare-dns.com/dns-query"),
        new DnsServerInfo("Google Public DNS", "Надежный глобальный DNS-сервер с высокой стабильностью работы", "8.8.8.8", "8.8.4.4", "https://dns.google/dns-query"),
        new DnsServerInfo("Yandex.DNS", "Быстрый публичный DNS-сервер от Яндекса с минимальной задержкой в РФ", "77.88.8.8", "77.88.8.1", ""),
        new DnsServerInfo("AdGuard DNS", "Альтернативный DNS с функцией блокировки рекламы, трекеров и фишинга", "94.140.14.14", "94.140.15.15", "https://dns.adguard-dns.com/dns-query")
    ];

    public static List<DnsServerInfo> GetAllServers(AppSettings? settings = null)
    {
        var result = new List<DnsServerInfo>(PredefinedDnsServers);
        if (settings?.EffectiveQuickDnsInTray == true && settings?.CustomDnsServers is { Count: > 0 } customList)
        {
            foreach (var c in customList)
            {
                result.Add(new DnsServerInfo(
                    c.Name,
                    string.IsNullOrWhiteSpace(c.Description) ? $"Пользовательский DNS ({c.Primary})" : c.Description,
                    c.Primary,
                    c.Secondary,
                    c.DohTemplate,
                    IsCustom: true
                ));
            }
        }
        return result;
    }

    public static NetworkInterface? GetActivePhysicalInterface()
    {
        try
        {
            var interfaces = NetworkInterface.GetAllNetworkInterfaces()
                .Where(ni =>
                    ni.OperationalStatus == OperationalStatus.Up &&
                    (ni.NetworkInterfaceType == NetworkInterfaceType.Ethernet || ni.NetworkInterfaceType == NetworkInterfaceType.Wireless80211) &&
                    ni.GetIPProperties().GatewayAddresses.Any(g =>
                        g.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork &&
                        !g.Address.ToString().StartsWith("0.0.0.0") &&
                        !g.Address.ToString().StartsWith("127.")))
                .ToList();

            static bool IsVirtual(NetworkInterface ni)
            {
                string desc = ni.Description;
                string name = ni.Name;
                return desc.Contains("VPN", StringComparison.OrdinalIgnoreCase) ||
                       desc.Contains("Radmin", StringComparison.OrdinalIgnoreCase) ||
                       desc.Contains("Virtual", StringComparison.OrdinalIgnoreCase) ||
                       desc.Contains("TAP", StringComparison.OrdinalIgnoreCase) ||
                       desc.Contains("Tun", StringComparison.OrdinalIgnoreCase) ||
                       desc.Contains("sing-tun", StringComparison.OrdinalIgnoreCase) ||
                       desc.Contains("WireGuard", StringComparison.OrdinalIgnoreCase) ||
                       desc.Contains("Hyper-V", StringComparison.OrdinalIgnoreCase) ||
                       desc.Contains("VMware", StringComparison.OrdinalIgnoreCase) ||
                       desc.Contains("VirtualBox", StringComparison.OrdinalIgnoreCase) ||
                       desc.Contains("Tailscale", StringComparison.OrdinalIgnoreCase) ||
                       desc.Contains("ZeroTier", StringComparison.OrdinalIgnoreCase) ||
                       desc.Contains("Wsl", StringComparison.OrdinalIgnoreCase) ||
                       name.Contains("VPN", StringComparison.OrdinalIgnoreCase) ||
                       name.Contains("Radmin", StringComparison.OrdinalIgnoreCase) ||
                       name.Contains("tun", StringComparison.OrdinalIgnoreCase) ||
                       name.Contains("tap", StringComparison.OrdinalIgnoreCase) ||
                       name.Contains("vEthernet", StringComparison.OrdinalIgnoreCase) ||
                       name.Contains("Loopback", StringComparison.OrdinalIgnoreCase);
            }

            var physical = interfaces.FirstOrDefault(ni => !IsVirtual(ni));
            return physical ?? interfaces.FirstOrDefault();
        }
        catch { }
        return null;
    }

    public static (bool IsDhcp, List<string> ConfiguredDns) GetPhysicalDnsSettings()
    {
        try
        {
            var activeInterface = GetActivePhysicalInterface();
            if (activeInterface is null) return (true, []);

            string? nameServer = null;
            try
            {
                using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                    $@"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces\{activeInterface.Id}");
                nameServer = key?.GetValue("NameServer") as string;
            }
            catch { }

            if (string.IsNullOrWhiteSpace(nameServer))
            {
                return (true, []);
            }

            var addresses = nameServer.Split([',', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                                      .Where(ip => System.Net.IPAddress.TryParse(ip, out _))
                                      .ToList();

            if (addresses.Count == 0)
            {
                return (true, []);
            }

            return (false, addresses);
        }
        catch
        {
            return (true, []);
        }
    }

    public static List<string> GetCurrentDnsAddresses()
    {
        var (isDhcp, configured) = GetPhysicalDnsSettings();
        return isDhcp ? [] : configured;
    }

    public static string DetectActiveDnsName(AppSettings? settings = null)
    {
        var (isDhcp, configured) = GetPhysicalDnsSettings();
        if (isDhcp || configured.Count == 0)
        {
            return "Системный (DHCP)";
        }

        var allServers = GetAllServers(settings);
        foreach (var server in allServers)
        {
            if (server.Primary != "dhcp" && configured.Contains(server.Primary))
            {
                return server.Name;
            }
        }

        return configured[0];
    }

    public static string DetectActiveDnsName(List<string> currentDns, AppSettings? settings = null)
    {
        return DetectActiveDnsName(settings);
    }

    public static async Task<bool> SetDnsServerAsync(string primary, string secondary, string dohTemplate = "")
    {
        try
        {
            var activeInterface = GetActivePhysicalInterface();
            if (activeInterface is null)
            {
                return false;
            }

            string interfaceName = activeInterface.Name;
            string interfaceId = activeInterface.Id;
            string psCommand;

            if (primary == "dhcp")
            {
                psCommand = $"Set-DnsClientServerAddress -InterfaceAlias '{interfaceName}' -ResetServerAddresses";
                string regPath = $"HKLM:\\SYSTEM\\CurrentControlSet\\Services\\Dnscache\\InterfaceSpecificParameters\\{interfaceId}";
                psCommand += $"; if (Test-Path '{regPath}\\DohInterfaceSettings') {{ Remove-Item -Path '{regPath}\\DohInterfaceSettings' -Recurse -Force }}" +
                             $"; Clear-DnsClientCache";
            }
            else
            {
                string addresses = string.IsNullOrEmpty(secondary) ? $"'{primary}'" : $"'{primary}', '{secondary}'";
                psCommand = $"Set-DnsClientServerAddress -InterfaceAlias '{interfaceName}' -ServerAddresses ({addresses})";

                if (!string.IsNullOrEmpty(dohTemplate))
                {
                    psCommand += $"; if (Get-Command Add-DnsClientDohServerAddress -ErrorAction SilentlyContinue) {{" +
                                 $" Add-DnsClientDohServerAddress -ServerAddress '{primary}' -DohTemplate '{dohTemplate}' -AllowFallbackToUdp $False -AutoUpgrade $True -ErrorAction SilentlyContinue";
                    if (!string.IsNullOrEmpty(secondary))
                    {
                        psCommand += $"; Add-DnsClientDohServerAddress -ServerAddress '{secondary}' -DohTemplate '{dohTemplate}' -AllowFallbackToUdp $False -AutoUpgrade $True -ErrorAction SilentlyContinue";
                    }
                    psCommand += " }";

                    string regPath = $"HKLM:\\SYSTEM\\CurrentControlSet\\Services\\Dnscache\\InterfaceSpecificParameters\\{interfaceId}";
                    psCommand += $"; if (!(Test-Path '{regPath}\\DohInterfaceSettings\\Doh\\{primary}')) {{ New-Item -Path '{regPath}\\DohInterfaceSettings\\Doh\\{primary}' -Force | Out-Null }}";
                    psCommand += $"; New-ItemProperty -Path '{regPath}\\DohInterfaceSettings\\Doh\\{primary}' -Name 'DohFlags' -Value 1 -PropertyType QWord -Force | Out-Null";

                    if (!string.IsNullOrEmpty(secondary))
                    {
                        psCommand += $"; if (!(Test-Path '{regPath}\\DohInterfaceSettings\\Doh\\{secondary}')) {{ New-Item -Path '{regPath}\\DohInterfaceSettings\\Doh\\{secondary}' -Force | Out-Null }}";
                        psCommand += $"; New-ItemProperty -Path '{regPath}\\DohInterfaceSettings\\Doh\\{secondary}' -Name 'DohFlags' -Value 1 -PropertyType QWord -Force | Out-Null";
                    }
                }

                psCommand += $"; Clear-DnsClientCache";
            }

            var psi = new ProcessStartInfo
            {
                FileName = "powershell",
                Arguments = $"-NoProfile -ExecutionPolicy Bypass -Command \"{psCommand}\"",
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            using var process = Process.Start(psi);
            if (process is not null)
            {
                await process.WaitForExitAsync();
                return process.ExitCode == 0;
            }
            return false;
        }
        catch
        {
            return false;
        }
    }
}

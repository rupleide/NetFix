using System.Collections.Concurrent;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using NetFix.Models;

namespace NetFix.Services;

public static class ConnectionAnalysisService
{
    private static readonly ConcurrentDictionary<string, ImageSource?> ProcessIconCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, HostsEntryModel> HostsCache = new(StringComparer.OrdinalIgnoreCase);
    private static DateTime _lastHostsRead = DateTime.MinValue;

    private static readonly HashSet<string> VpnKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "wireguard", "wintun", "tap", "tun", "openvpn", "tailscale",
        "cloudflare", "warp", "zerotier", "vpn", "pptp", "l2tp", "sstp", "softether", "ipsec", "amnezia"
    };

    private static readonly HashSet<string> VirtualKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "virtual", "hyper-v", "vethernet", "vmware", "virtualbox", "loopback", "npcap", "winpcap", "docker"
    };

    private static readonly HashSet<string> CommonAppKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "telegram", "discord", "chrome", "firefox", "msedge", "opera", "brave",
        "steam", "steamwebhelper", "spotify", "epicgameslauncher", "battle.net", "yandex"
    };

    private static readonly HashSet<string> SystemNoiseProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "idle", "system", "svchost", "csrss", "wininit", "winlogon", "lsass",
        "services", "smss", "fontdrvhost", "sihost", "dwm", "taskhostw", "dllhost",
        "runtimebroker", "searchhost", "startmenuexperiencehost", "conhost",
        "textinputhost", "ctfmon", "securityhealthservice", "systemsettings",
        "applicationframehost", "shellexperiencehost", "spoolsv", "wudfhost",
        "smartscreen", "searchindexer", "audiodg", "dasHost", "standardcollector.service"
    };

    private static readonly string[] HelperKeywords =
    [
        "webhelper", "helper", "service", "updater", "crashhandler", "broadcast", "overlay", "daemon", "agent"
    ];

    private static readonly ConcurrentDictionary<int, (string ExePath, string FriendlyName)> ProcessInfoCache = new();

    #region Process Listing & Grouping

    public static List<ProcessItemModel> GetRunningProcesses()
    {
        var allSockets = GetRawSocketsForPid(0);
        var pidSocketCounts = allSockets.GroupBy(s => s.ProcessId).ToDictionary(g => g.Key, g => g.Count());

        var running = Process.GetProcesses();
        var groupedApps = new Dictionary<string, ProcessGroupingInfo>(StringComparer.OrdinalIgnoreCase);

        foreach (var p in running)
        {
            try
            {
                if (p.Id <= 4) continue;
                string rawName = p.ProcessName;

                string windowTitle = "";
                bool hasWindow = false;
                try
                {
                    windowTitle = p.MainWindowTitle?.Trim() ?? "";
                    hasWindow = p.MainWindowHandle != IntPtr.Zero && !string.IsNullOrWhiteSpace(windowTitle);
                }
                catch { }

                if (SystemNoiseProcesses.Contains(rawName) && !hasWindow)
                {
                    continue;
                }

                string exePath;
                string friendlyName;

                if (ProcessInfoCache.TryGetValue(p.Id, out var cachedInfo))
                {
                    exePath = cachedInfo.ExePath;
                    friendlyName = cachedInfo.FriendlyName;
                }
                else
                {
                    exePath = "";
                    try
                    {
                        exePath = p.MainModule?.FileName ?? "";
                    }
                    catch { }

                    if (string.IsNullOrEmpty(exePath))
                    {
                        exePath = rawName + ".exe";
                    }

                    friendlyName = GetFriendlyAppName(rawName, exePath, windowTitle);
                    ProcessInfoCache[p.Id] = (exePath, friendlyName);
                }

                string appKey = NormalizeAppKey(rawName);

                bool isCommon = CommonAppKeys.Contains(rawName) || CommonAppKeys.Contains(appKey);
                int socketCount = pidSocketCounts.TryGetValue(p.Id, out int cnt) ? cnt : 0;

                if (!groupedApps.TryGetValue(appKey, out var group))
                {
                    group = new ProcessGroupingInfo
                    {
                        AppKey = appKey,
                        FriendlyName = friendlyName,
                        ExePath = exePath,
                        IsCommon = isCommon,
                        HasWindow = hasWindow,
                        WindowTitle = windowTitle
                    };
                    groupedApps[appKey] = group;
                }

                group.ProcessIds.Add(p.Id);
                group.TotalSocketCount += socketCount;

                if (!group.HasWindow && hasWindow)
                {
                    group.HasWindow = true;
                    group.WindowTitle = windowTitle;
                }

                if (string.IsNullOrEmpty(group.ExePath) && !string.IsNullOrEmpty(exePath))
                {
                    group.ExePath = exePath;
                }
            }
            catch
            {
            }
        }

        var result = new List<ProcessItemModel>();

        foreach (var group in groupedApps.Values)
        {
            if (!group.HasWindow && !group.IsCommon && group.TotalSocketCount == 0)
            {
                continue;
            }

            var icon = GetIconForProcess(group.ExePath);

            result.Add(new ProcessItemModel
            {
                AppName = group.FriendlyName,
                DisplayName = group.FriendlyName,
                ExePath = group.ExePath,
                AppKey = group.AppKey,
                Icon = icon,
                ProcessIds = group.ProcessIds,
                WindowTitle = group.WindowTitle,
                ConnectionCount = group.TotalSocketCount,
                IsCommonApp = group.IsCommon,
                HasWindow = group.HasWindow
            });
        }

        return result
            .OrderByDescending(p => p.IsCommonApp)
            .ThenByDescending(p => p.HasWindow)
            .ThenByDescending(p => p.ConnectionCount)
            .ThenBy(p => p.AppName)
            .ToList();
    }

    private class ProcessGroupingInfo
    {
        public string AppKey { get; set; } = "";
        public string FriendlyName { get; set; } = "";
        public string ExePath { get; set; } = "";
        public List<int> ProcessIds { get; } = [];
        public bool IsCommon { get; set; }
        public bool HasWindow { get; set; }
        public string WindowTitle { get; set; } = "";
        public int TotalSocketCount { get; set; }
    }

    private static string NormalizeAppKey(string procName)
    {
        string name = procName.ToLowerInvariant();
        if (name.StartsWith("discord")) return "discord";
        if (name.StartsWith("telegram")) return "telegram";
        if (name.StartsWith("steam")) return "steam";
        if (name.StartsWith("chrome")) return "chrome";
        if (name.StartsWith("msedge")) return "msedge";
        if (name.StartsWith("firefox")) return "firefox";
        if (name.StartsWith("opera")) return "opera";
        if (name.StartsWith("brave")) return "brave";
        if (name.StartsWith("spotify")) return "spotify";
        if (name.StartsWith("yandex")) return "yandex";
        return name;
    }

    private static string GetFriendlyAppName(string procName, string exePath, string windowTitle)
    {
        string nameLower = procName.ToLowerInvariant();
        if (nameLower.StartsWith("discord")) return "Discord";
        if (nameLower.StartsWith("telegram")) return "Telegram";
        if (nameLower.StartsWith("chrome")) return "Google Chrome";
        if (nameLower.StartsWith("msedge")) return "Microsoft Edge";
        if (nameLower.StartsWith("firefox")) return "Mozilla Firefox";
        if (nameLower.StartsWith("opera")) return "Opera";
        if (nameLower.StartsWith("brave")) return "Brave Browser";
        if (nameLower.StartsWith("steam")) return "Steam";
        if (nameLower.StartsWith("spotify")) return "Spotify";
        if (nameLower.StartsWith("yandex")) return "Яндекс Браузер";
        if (nameLower.StartsWith("epicgames")) return "Epic Games";

        if (!string.IsNullOrEmpty(exePath) && File.Exists(exePath))
        {
            try
            {
                var vi = FileVersionInfo.GetVersionInfo(exePath);
                if (!string.IsNullOrWhiteSpace(vi.FileDescription))
                {
                    return vi.FileDescription.Trim();
                }
            }
            catch { }
        }

        return procName;
    }

    private static ImageSource? GetIconForProcess(string path)
    {
        if (string.IsNullOrEmpty(path)) return null;

        if (ProcessIconCache.TryGetValue(path, out var cached))
        {
            return cached;
        }

        try
        {
            if (File.Exists(path))
            {
                using var icon = Icon.ExtractAssociatedIcon(path);
                if (icon is not null)
                {
                    var bitmap = Imaging.CreateBitmapSourceFromHIcon(
                        icon.Handle,
                        Int32Rect.Empty,
                        BitmapSizeOptions.FromEmptyOptions());
                    bitmap.Freeze();
                    ProcessIconCache[path] = bitmap;
                    return bitmap;
                }
            }
        }
        catch { }

        ProcessIconCache[path] = null;
        return null;
    }

    #endregion

    #region Hosts File Management

    public static Dictionary<string, HostsEntryModel> GetHostsEntries()
    {
        if (DateTime.UtcNow - _lastHostsRead < TimeSpan.FromSeconds(5) && !HostsCache.IsEmpty)
        {
            return new Dictionary<string, HostsEntryModel>(HostsCache, StringComparer.OrdinalIgnoreCase);
        }

        var result = new Dictionary<string, HostsEntryModel>(StringComparer.OrdinalIgnoreCase);
        HostsCache.Clear();

        try
        {
            string hostsPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"drivers\etc\hosts");
            if (File.Exists(hostsPath))
            {
                var lines = File.ReadAllLines(hostsPath);
                bool isNetFixSection = false;

                foreach (var rawLine in lines)
                {
                    string line = rawLine.Trim();
                    if (string.IsNullOrWhiteSpace(line)) continue;

                    if (line.Contains("NetFix", StringComparison.OrdinalIgnoreCase))
                    {
                        isNetFixSection = true;
                    }

                    if (line.StartsWith('#')) continue;

                    var parts = line.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length >= 2)
                    {
                        string ip = parts[0];
                        for (int i = 1; i < parts.Length; i++)
                        {
                            string hostname = parts[i].Trim();
                            if (hostname.StartsWith('#')) break;

                            var entry = new HostsEntryModel
                            {
                                Ip = ip,
                                Hostname = hostname,
                                IsNetFixManaged = isNetFixSection || hostname.Contains("telegram") || hostname.Contains("discord")
                            };
                            result[hostname] = entry;
                            result[ip] = entry;
                            HostsCache[hostname] = entry;
                            HostsCache[ip] = entry;
                        }
                    }
                }
            }
        }
        catch { }

        _lastHostsRead = DateTime.UtcNow;
        return result;
    }

    #endregion

    #region Sockets & Connections Inspection

    private static readonly ConcurrentDictionary<string, (ulong TotalBytes, DateTime Timestamp, double SmoothedSpeed)> SocketTrafficHistory = new();

    public static (List<ConnectionDetailModel> Connections, ConnectionSummaryModel Summary) GetConnectionsForProcess(
        IEnumerable<int> pids,
        DnsEtwMonitor? etwMonitor,
        int tgWsProxyPort,
        bool isTgWsRunning,
        bool isZapretRunning,
        string zapretConfigName,
        string targetAppKey = "")
    {
        var pidSet = new HashSet<int>(pids);
        var rawConnections = GetRawSocketsForPids(pidSet);
        var hosts = GetHostsEntries();
        var adapters = GetAdaptersList();

        var pidProcessInfo = new Dictionary<int, (string Name, string ExePath)>();
        foreach (int pid in pidSet)
        {
            try
            {
                using var p = Process.GetProcessById(pid);
                string exe = "";
                try { exe = p.MainModule?.FileName ?? ""; } catch { }
                pidProcessInfo[pid] = (p.ProcessName, exe);
            }
            catch
            {
                pidProcessInfo[pid] = ("", "");
            }
        }

        string normAppKey = (targetAppKey ?? "").Trim().ToLowerInvariant();
        if (normAppKey.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            normAppKey = normAppKey[..^4];
        }

        var details = new List<ConnectionDetailModel>();
        int vpnCount = 0;
        int directCount = 0;
        int hostsCount = 0;
        int proxyCount = 0;
        int zapretCount = 0;

        DateTime now = DateTime.UtcNow;

        foreach (var socket in rawConnections)
        {
            string domain = "";
            bool isHosts = false;
            string dnsSource = "DNS / IP";
            string dnsDetails = "";

            if (etwMonitor is not null)
            {
                var etwDomain = etwMonitor.GetDomainForEndpoint(socket.ProcessId, socket.RemoteAddress);
                if (!string.IsNullOrEmpty(etwDomain))
                {
                    domain = etwDomain;
                    dnsSource = "ETW (DNS-Client)";
                }
            }

            if (hosts.TryGetValue(socket.RemoteAddress, out var hostsEntry) ||
                (!string.IsNullOrEmpty(domain) && hosts.TryGetValue(domain, out hostsEntry)))
            {
                isHosts = true;
                domain = string.IsNullOrEmpty(domain) ? hostsEntry.Hostname : domain;
                dnsSource = hostsEntry.IsNetFixManaged ? "hosts (NetFix)" : "hosts-файл";
                dnsDetails = $"Запись в hosts: {hostsEntry.Hostname} ➔ {hostsEntry.Ip}";
                hostsCount++;
            }
            else if (!string.IsNullOrEmpty(domain))
            {
                dnsDetails = $"Резолв через DNS (перехвачено ETW): {domain} ➔ {socket.RemoteAddress}";
            }
            else
            {
                dnsDetails = $"Прямой IP адрес {socket.RemoteAddress}:{socket.RemotePort}";
            }

            bool isProxy = false;
            string proxyName = "Прямое";
            string proxyDetails = "Прямое соединение без локального прокси";

            if ((socket.RemoteAddress == "127.0.0.1" || socket.RemoteAddress == "::1") &&
                socket.RemotePort == tgWsProxyPort && isTgWsRunning)
            {
                isProxy = true;
                proxyName = "TgWsProxy";
                proxyDetails = $"Перенаправлено через TgWsProxy (127.0.0.1:{tgWsProxyPort})";
                proxyCount++;
            }

            var routingInfo = ResolveRoutingForEndpoint(socket.RemoteAddress, adapters);
            if (routingInfo.IsVpn)
            {
                vpnCount++;
            }
            else
            {
                directCount++;
            }

            bool zapretActive = false;
            string matchedRule = "Не перехватывается";
            string packetDetails = "WinDivert не перехватывает этот порт";

            if (isZapretRunning)
            {
                if (socket.RemotePort is 80 or 443 or 5222 || (socket.RemotePort >= 50000 && socket.RemotePort <= 50010))
                {
                    zapretActive = true;
                    matchedRule = $"Порт {socket.RemotePort} (TCP/UDP)";
                    packetDetails = $"WinDivert активен. Трафик защищён Zapret (конфиг: {zapretConfigName})";
                    zapretCount++;
                }
                else
                {
                    packetDetails = $"Порт {socket.RemotePort} не входит в список фильтрации Zapret";
                }
            }

            string connKey = $"{socket.Protocol}_{socket.LocalAddress}:{socket.LocalPort}->{socket.RemoteAddress}:{socket.RemotePort}";
            ulong currentTotal = socket.BytesIn + socket.BytesOut;
            double delta = 0;
            double bytesPerSec = 0;

            if (SocketTrafficHistory.TryGetValue(connKey, out var prev))
            {
                double elapsed = (now - prev.Timestamp).TotalSeconds;
                if (elapsed >= 0.2 && currentTotal >= prev.TotalBytes)
                {
                    delta = currentTotal - prev.TotalBytes;
                    double instantSpeed = delta / elapsed;

                    if (instantSpeed > 1_000_000_000) instantSpeed = 0;

                    bytesPerSec = prev.SmoothedSpeed > 0
                        ? (prev.SmoothedSpeed * 0.5) + (instantSpeed * 0.5)
                        : instantSpeed;
                }
                else if (elapsed >= 0.2)
                {
                    bytesPerSec = prev.SmoothedSpeed * 0.4;
                }
                else
                {
                    bytesPerSec = prev.SmoothedSpeed;
                }
            }
            else
            {
                delta = 0;
                bytesPerSec = 0;
            }

            if (bytesPerSec < 80) bytesPerSec = 0;

            SocketTrafficHistory[connKey] = (currentTotal, now, bytesPerSec);

            string procName = "";
            string procExe = "";
            if (pidProcessInfo.TryGetValue(socket.ProcessId, out var pInfo))
            {
                procName = pInfo.Name;
                procExe = pInfo.ExePath;
            }

            string normProcName = procName.ToLowerInvariant();

            bool isMainProc = !string.IsNullOrEmpty(normAppKey) &&
                (normProcName.Equals(normAppKey, StringComparison.OrdinalIgnoreCase) ||
                 normProcName.StartsWith(normAppKey + ".", StringComparison.OrdinalIgnoreCase));

            bool isSecondary = !string.IsNullOrEmpty(normProcName) &&
                HelperKeywords.Any(k => normProcName.Contains(k, StringComparison.OrdinalIgnoreCase));

            var item = new ConnectionDetailModel
            {
                Protocol = socket.Protocol,
                LocalAddress = socket.LocalAddress,
                LocalPort = socket.LocalPort,
                RemoteAddress = socket.RemoteAddress,
                RemotePort = socket.RemotePort,
                State = socket.State,
                ProcessId = socket.ProcessId,
                ProcessName = procName,
                ExecutablePath = string.IsNullOrEmpty(procExe) ? null : procExe,
                IsMainProcessOfGroup = isMainProc,
                IsSecondaryProcess = isSecondary,
                TotalBytesIn = socket.BytesIn,
                TotalBytesOut = socket.BytesOut,
                DeltaBytes = delta,
                BytesPerSec = bytesPerSec,
                Dns = new LayerDnsInfo
                {
                    Domain = domain,
                    Source = dnsSource,
                    IsHosts = isHosts,
                    Details = dnsDetails
                },
                Routing = routingInfo,
                PacketFilter = new LayerPacketFilterInfo
                {
                    IsZapretActive = zapretActive,
                    ServiceStatus = isZapretRunning ? "Запущен" : "Остановлен",
                    ConfigName = zapretConfigName,
                    MatchedRule = matchedRule,
                    Details = packetDetails
                },
                Proxy = new LayerProxyInfo
                {
                    HasProxy = isProxy,
                    ProxyName = proxyName,
                    ProxyPort = isProxy ? tgWsProxyPort : 0,
                    Details = proxyDetails
                }
            };

            details.Add(item);
        }

        if (SocketTrafficHistory.Count > 1000)
        {
            var cutoff = now.AddMinutes(-5);
            foreach (var kvp in SocketTrafficHistory)
            {
                if (kvp.Value.Timestamp < cutoff)
                {
                    SocketTrafficHistory.TryRemove(kvp.Key, out _);
                }
            }
        }

        RankAndSortConnections(details);

        string summaryText = details.Count == 0
            ? "Нет активных соединений"
            : $"Активно: {details.Count} | {vpnCount} через VPN, {directCount} напрямую" +
              (hostsCount > 0 ? $", {hostsCount} через hosts" : "") +
              (proxyCount > 0 ? $", {proxyCount} через TgWsProxy" : "");

        var summary = new ConnectionSummaryModel
        {
            TotalCount = details.Count,
            VpnCount = vpnCount,
            DirectCount = directCount,
            HostsCount = hostsCount,
            ProxyCount = proxyCount,
            ZapretCount = zapretCount,
            SummaryText = summaryText
        };

        return (details, summary);
    }

    private static void RankAndSortConnections(List<ConnectionDetailModel> list)
    {
        if (list.Count == 0) return;

        foreach (var c in list) c.IsPrimary = false;

        var nonLoopbackNonListening = list.Where(c => c.State != "LISTENING" && !c.IsLoopback).ToList();
        var candidates = nonLoopbackNonListening.Count > 0 ? nonLoopbackNonListening : list;

        var primary = candidates
            .OrderByDescending(c => c.IsMainProcessOfGroup)
            .ThenByDescending(c => !c.IsSecondaryProcess)
            .ThenByDescending(c => GetEstablishedExternalScore(c))
            .ThenByDescending(c => c.BytesPerSec > 0)
            .ThenByDescending(c => c.BytesPerSec)
            .ThenByDescending(c => c.TotalBytes)
            .ThenByDescending(c => GetModifierScore(c))
            .ThenBy(c => c.RemoteAddress)
            .FirstOrDefault();

        if (primary is not null)
        {
            primary.IsPrimary = true;
            list.Remove(primary);
            list.Insert(0, primary);
        }
    }

    private static int GetEstablishedExternalScore(ConnectionDetailModel c)
    {
        int score = 0;
        if (c.State == "ESTABLISHED" && !c.IsLoopback && !c.IsPrivateIp) score = 1000;
        else if (c.State == "ESTABLISHED" && !c.IsLoopback) score = 750;
        else if (c.State == "ESTABLISHED") score = 500;
        else if (c.State is "SYN_SENT" or "SYN_RCVD") score = 400;
        else if (c.State is "TIME_WAIT" or "CLOSE_WAIT") score = 200;
        else if (c.State != "LISTENING") score = 100;

        return score;
    }

    private static int GetModifierScore(ConnectionDetailModel c)
    {
        int score = 0;
        if (c.PacketFilter.IsZapretActive) score += 100;
        if (c.Routing.IsVpn) score += 100;
        if (c.Proxy.HasProxy) score += 100;
        return score;
    }

    private static LayerRoutingInfo ResolveRoutingForEndpoint(string remoteIp, List<NetworkAdapterInfo> adapters)
    {
        if (remoteIp is "127.0.0.1" or "::1" or "0.0.0.0")
        {
            return new LayerRoutingInfo
            {
                AdapterName = "Loopback",
                AdapterDescription = "Локальный интерфейс",
                AdapterType = "Локальный",
                IsVpn = false,
                InterfaceIndex = 1,
                Gateway = "127.0.0.1",
                Details = "Локальный сокет (трафик не покидает систему)"
            };
        }

        try
        {
            if (IPAddress.TryParse(remoteIp, out var ip) && ip.AddressFamily == AddressFamily.InterNetwork)
            {
                byte[] bytes = ip.GetAddressBytes();
                uint destAddr = BitConverter.ToUInt32(bytes, 0);

                if (GetBestInterface(destAddr, out uint ifIndex) == 0)
                {
                    var matched = adapters.FirstOrDefault(a => a.Index == ifIndex);
                    if (matched is not null)
                    {
                        return new LayerRoutingInfo
                        {
                            AdapterName = matched.Name,
                            AdapterDescription = matched.Description,
                            AdapterType = matched.Type,
                            IsVpn = matched.IsVpn,
                            InterfaceIndex = ifIndex,
                            Gateway = matched.Gateways,
                            Details = $"Исходящий маршрут через [{matched.Type}] {matched.Name} (шлюз: {matched.Gateways})"
                        };
                    }
                }
            }
        }
        catch { }

        var defaultAdapter = adapters.FirstOrDefault(a => a.IsDefaultGateway) ?? adapters.FirstOrDefault();
        return new LayerRoutingInfo
        {
            AdapterName = defaultAdapter?.Name ?? "Основной адаптер",
            AdapterDescription = defaultAdapter?.Description ?? "",
            AdapterType = defaultAdapter?.Type ?? "Физический",
            IsVpn = defaultAdapter?.IsVpn ?? false,
            InterfaceIndex = defaultAdapter?.Index ?? 0,
            Gateway = defaultAdapter?.Gateways ?? "",
            Details = $"Маршрут по умолчанию через {defaultAdapter?.Name}"
        };
    }

    #endregion

    #region System Overview

    private static readonly ConcurrentDictionary<(int Pid, long StartTicks), (TimeSpan CpuTime, DateTime Timestamp)> ProcessCpuHistory = new();
    private static readonly ConcurrentDictionary<int, (ulong TotalBytes, DateTime Timestamp, double SmoothedSpeed)> ProcessNetworkHistory = new();

    public static void ResetCpuHistory()
    {
        ProcessCpuHistory.Clear();
        ProcessNetworkHistory.Clear();
        ProcessInfoCache.Clear();
    }

    public static SystemOverviewModel GetSystemOverview(int tgWsProxyPort, string zapretConfigName)
    {
        var adapters = GetAdaptersList();
        var defaultAdapter = adapters.FirstOrDefault(a => a.IsDefaultGateway);
        var hosts = GetHostsEntries();

        bool winDivertLoaded = IsWinDivertLoaded();
        string zapretStatus = winDivertLoaded ? "Активен (WinDivert загружен)" : "Остановлен";

        bool tgWsRunning = Process.GetProcessesByName("TgWsProxy").Length > 0;
        int tgWsConns = CountConnectionsToPort(tgWsProxyPort);

        var allDns = adapters
            .SelectMany(a => a.DnsServers.Split(',', StringSplitOptions.RemoveEmptyEntries))
            .Select(d => d.Trim())
            .Distinct()
            .ToList();

        var activeProcesses = GetActiveProcessesActivity(adapters, hosts, winDivertLoaded, tgWsProxyPort);

        return new SystemOverviewModel
        {
            Adapters = adapters,
            DefaultRouteAdapter = defaultAdapter != null ? $"{defaultAdapter.Name} ({defaultAdapter.Type})" : "Не определён",
            DnsServers = allDns,
            HostsEntries = hosts.Values.DistinctBy(h => h.Hostname).ToList(),
            WinDivertLoaded = winDivertLoaded,
            ZapretStatus = zapretStatus,
            ZapretConfig = string.IsNullOrEmpty(zapretConfigName) ? "general (ALT2).bat" : zapretConfigName,
            TgWsProxyRunning = tgWsRunning,
            TgWsProxyPort = tgWsProxyPort,
            TgWsProxyConnectionsCount = tgWsConns,
            ActiveProcesses = activeProcesses
        };
    }

    private static List<SystemProcessActivityModel> GetActiveProcessesActivity(List<NetworkAdapterInfo> adapters, Dictionary<string, HostsEntryModel> hosts, bool isZapretRunning, int tgWsPort)
    {
        var result = new List<SystemProcessActivityModel>();
        var allSockets = GetRawSocketsForPid(0);
        var socketsByPid = allSockets.GroupBy(s => s.ProcessId).ToDictionary(g => g.Key, g => g.ToList());

        DateTime now = DateTime.UtcNow;
        int cpuCores = Math.Max(1, Environment.ProcessorCount);

        var vpnAdapterIps = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var ad in adapters.Where(a => a.IsVpn))
        {
            foreach (var ip in ad.IpAddresses.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                vpnAdapterIps.Add(ip);
            }
        }

        var activePidKeys = new HashSet<(int Pid, long StartTicks)>();
        var activePids = new HashSet<int>();

        foreach (var (pid, sockets) in socketsByPid)
        {
            if (pid <= 4 || sockets.Count == 0) continue;
            activePids.Add(pid);

            string procName = "";
            string windowTitle = "";
            string exePath = "";
            long ramBytes = 0;
            double? cpuPercent = null;
            long startTicks = 0;

            try
            {
                using var p = Process.GetProcessById(pid);
                procName = p.ProcessName;

                try
                {
                    windowTitle = p.MainWindowTitle?.Trim() ?? "";
                }
                catch { }

                try
                {
                    exePath = p.MainModule?.FileName ?? "";
                }
                catch { }

                try
                {
                    ramBytes = p.WorkingSet64;
                }
                catch { }

                try
                {
                    startTicks = p.StartTime.Ticks;
                    var cpuTime = p.TotalProcessorTime;
                    var key = (pid, startTicks);
                    activePidKeys.Add(key);

                    if (ProcessCpuHistory.TryGetValue(key, out var prev))
                    {
                        double elapsedMs = (now - prev.Timestamp).TotalMilliseconds;
                        if (elapsedMs >= 400)
                        {
                            double cpuDeltaMs = (cpuTime - prev.CpuTime).TotalMilliseconds;
                            if (cpuDeltaMs >= 0)
                            {
                                double calculated = (cpuDeltaMs / (elapsedMs * cpuCores)) * 100.0;
                                cpuPercent = Math.Clamp(calculated, 0.0, 100.0);
                                ProcessCpuHistory[key] = (cpuTime, now);
                            }
                        }
                    }
                    else
                    {
                        ProcessCpuHistory[key] = (cpuTime, now);
                        cpuPercent = null;
                    }
                }
                catch
                {
                    cpuPercent = null;
                }
            }
            catch
            {
                continue;
            }

            if (string.IsNullOrEmpty(procName)) continue;

            bool hasVpn = false;
            bool hasZapret = false;
            bool hasProxy = false;
            bool hasHosts = false;

            ulong procTotalBytes = 0;

            foreach (var s in sockets)
            {
                if (vpnAdapterIps.Contains(s.LocalAddress))
                {
                    hasVpn = true;
                }

                if (s.RemotePort == tgWsPort || s.LocalPort == tgWsPort)
                {
                    hasProxy = true;
                }

                if (isZapretRunning && (s.RemotePort == 80 || s.RemotePort == 443 || (s.RemotePort >= 50000 && s.RemotePort <= 50010)))
                {
                    hasZapret = true;
                }

                if (hosts.ContainsKey(s.RemoteAddress))
                {
                    hasHosts = true;
                }

                procTotalBytes += s.BytesIn + s.BytesOut;
            }

            double procBytesPerSec = 0;
            if (ProcessNetworkHistory.TryGetValue(pid, out var prevNet))
            {
                double elapsedSec = (now - prevNet.Timestamp).TotalSeconds;
                if (elapsedSec >= 0.2 && procTotalBytes >= prevNet.TotalBytes)
                {
                    double delta = procTotalBytes - prevNet.TotalBytes;
                    double instantSpeed = delta / elapsedSec;
                    if (instantSpeed < 2_000_000_000)
                    {
                        procBytesPerSec = prevNet.SmoothedSpeed > 0
                            ? (prevNet.SmoothedSpeed * 0.4) + (instantSpeed * 0.6)
                            : instantSpeed;
                    }
                }
                else if (elapsedSec >= 0.2)
                {
                    procBytesPerSec = prevNet.SmoothedSpeed * 0.3;
                }
                else
                {
                    procBytesPerSec = prevNet.SmoothedSpeed;
                }
            }

            if (procBytesPerSec < 50) procBytesPerSec = 0;
            ProcessNetworkHistory[pid] = (procTotalBytes, now, procBytesPerSec);

            var icon = GetIconForProcess(exePath);

            var modifiers = new List<string>();
            if (hasZapret) modifiers.Add("Zapret");
            if (hasProxy) modifiers.Add("TgWsProxy");
            if (hasHosts) modifiers.Add("Hosts");

            result.Add(new SystemProcessActivityModel
            {
                ProcessId = pid,
                ProcessName = procName,
                WindowTitle = windowTitle,
                Icon = icon,
                CpuPercent = cpuPercent,
                RamBytes = ramBytes,
                BytesPerSec = procBytesPerSec,
                TotalBytes = procTotalBytes,
                SocketsCount = sockets.Count,
                PrimaryRoute = hasVpn ? "VPN" : "Прямой",
                RouteModifiers = modifiers
            });
        }

        if (ProcessCpuHistory.Count > 200)
        {
            foreach (var k in ProcessCpuHistory.Keys)
            {
                if (!activePidKeys.Contains(k))
                {
                    ProcessCpuHistory.TryRemove(k, out _);
                }
            }
        }

        if (ProcessNetworkHistory.Count > 200)
        {
            foreach (var k in ProcessNetworkHistory.Keys)
            {
                if (!activePids.Contains(k))
                {
                    ProcessNetworkHistory.TryRemove(k, out _);
                }
            }
        }

        return result;
    }

    private static bool IsWinDivertLoaded()
    {
        try
        {
            if (Process.GetProcessesByName("winws").Length > 0) return true;
            if (Process.GetProcessesByName("windivert").Length > 0) return true;
            if (Process.GetProcessesByName("nfqws").Length > 0) return true;
        }
        catch { }

        return false;
    }

    private static int CountConnectionsToPort(int port)
    {
        try
        {
            var tcpConnections = GetRawSocketsForPid(0);
            return tcpConnections.Count(c => c.LocalPort == port || c.RemotePort == port);
        }
        catch
        {
            return 0;
        }
    }

    public static List<NetworkAdapterInfo> GetAdaptersList()
    {
        var result = new List<NetworkAdapterInfo>();
        try
        {
            var interfaces = NetworkInterface.GetAllNetworkInterfaces();

            uint defaultIfIndex = 0;
            if (GetBestInterface(0x08080808, out uint bestIdx) == 0)
            {
                defaultIfIndex = bestIdx;
            }

            foreach (var nic in interfaces)
            {
                if (nic.OperationalStatus != OperationalStatus.Up &&
                    nic.NetworkInterfaceType != NetworkInterfaceType.Wireless80211 &&
                    nic.NetworkInterfaceType != NetworkInterfaceType.Ethernet)
                {
                    continue;
                }

                var ipProps = nic.GetIPProperties();
                var ipv4Props = ipProps.GetIPv4Properties();
                uint ifIndex = (uint)(ipv4Props?.Index ?? 0);

                string desc = nic.Description;
                string name = nic.Name;

                bool isVpn = VpnKeywords.Any(k => desc.Contains(k, StringComparison.OrdinalIgnoreCase) ||
                                                  name.Contains(k, StringComparison.OrdinalIgnoreCase));

                bool isVirtual = !isVpn && VirtualKeywords.Any(k => desc.Contains(k, StringComparison.OrdinalIgnoreCase) ||
                                                                   name.Contains(k, StringComparison.OrdinalIgnoreCase));

                string type;
                if (isVpn)
                {
                    type = "VPN";
                }
                else if (isVirtual)
                {
                    type = "Виртуальный";
                }
                else if (nic.NetworkInterfaceType == NetworkInterfaceType.Wireless80211 ||
                         name.Contains("Wi-Fi", StringComparison.OrdinalIgnoreCase))
                {
                    type = "Wi-Fi";
                }
                else
                {
                    type = "Ethernet";
                }

                var unicast = string.Join(", ", ipProps.UnicastAddresses
                    .Where(u => u.Address.AddressFamily == AddressFamily.InterNetwork)
                    .Select(u => u.Address.ToString()));

                var gateways = string.Join(", ", ipProps.GatewayAddresses
                    .Select(g => g.Address.ToString()));

                var dns = string.Join(", ", ipProps.DnsAddresses
                    .Select(d => d.ToString()));

                bool isDefault = (ifIndex == defaultIfIndex && defaultIfIndex > 0) ||
                                 (!string.IsNullOrEmpty(gateways) && !isVirtual);

                result.Add(new NetworkAdapterInfo
                {
                    Index = ifIndex,
                    Name = name,
                    Description = desc,
                    Type = type,
                    IsVpn = isVpn,
                    IsDefaultGateway = isDefault,
                    IpAddresses = string.IsNullOrEmpty(unicast) ? "Нет IPv4" : unicast,
                    Gateways = string.IsNullOrEmpty(gateways) ? "—" : gateways,
                    DnsServers = string.IsNullOrEmpty(dns) ? "—" : dns,
                    Status = nic.OperationalStatus == OperationalStatus.Up ? "Подключено" : "Отключено"
                });
            }
        }
        catch { }

        return result
            .OrderByDescending(a => a.IsDefaultGateway)
            .ThenByDescending(a => a.IsVpn)
            .ThenBy(a => a.Name)
            .ToList();
    }

    #endregion

    #region Win32 IP Helper P/Invoke

    internal record RawSocketInfo(string Protocol, string LocalAddress, int LocalPort, string RemoteAddress, int RemotePort, string State, int ProcessId, ulong BytesIn = 0, ulong BytesOut = 0);

    internal static List<RawSocketInfo> GetRawSocketsForPid(int targetPid)
    {
        return GetRawSocketsForPids(targetPid == 0 ? null : [targetPid]);
    }

    private static List<RawSocketInfo> GetRawSocketsForPids(HashSet<int>? pids)
    {
        var result = new List<RawSocketInfo>();

        GetTcpConnections(AF_INET, pids, result);
        GetTcpConnections(AF_INET6, pids, result);
        GetUdpConnections(AF_INET, pids, result);
        GetUdpConnections(AF_INET6, pids, result);

        return result;
    }

    private static void GetTcpConnections(uint af, HashSet<int>? targetPids, List<RawSocketInfo> result)
    {
        int size = 0;
        uint status = GetExtendedTcpTable(IntPtr.Zero, ref size, true, af, TCP_TABLE_CLASS.TCP_TABLE_OWNER_PID_ALL, 0);
        if (size == 0) return;

        IntPtr buffer = Marshal.AllocHGlobal(size);
        try
        {
            status = GetExtendedTcpTable(buffer, ref size, true, af, TCP_TABLE_CLASS.TCP_TABLE_OWNER_PID_ALL, 0);
            if (status != 0) return;

            int numEntries = Marshal.ReadInt32(buffer);
            IntPtr rowPtr = IntPtr.Add(buffer, 4);

            if (af == AF_INET)
            {
                int rowSize = Marshal.SizeOf<MIB_TCPROW_OWNER_PID>();
                for (int i = 0; i < numEntries; i++)
                {
                    var row = Marshal.PtrToStructure<MIB_TCPROW_OWNER_PID>(rowPtr);
                    int pid = (int)row.dwOwningPid;
                    if (targetPids == null || targetPids.Contains(pid))
                    {
                        var localIp = new IPAddress(row.dwLocalAddr).ToString();
                        var remoteIp = new IPAddress(row.dwRemoteAddr).ToString();
                        int localPort = (ushort)IPAddress.NetworkToHostOrder((short)row.dwLocalPort);
                        int remotePort = (ushort)IPAddress.NetworkToHostOrder((short)row.dwRemotePort);
                        string state = GetTcpStateString(row.dwState);

                        var (bytesIn, bytesOut) = QueryTcpStats(ref row);

                        result.Add(new RawSocketInfo("TCP", localIp, localPort, remoteIp, remotePort, state, pid, bytesIn, bytesOut));
                    }
                    rowPtr = IntPtr.Add(rowPtr, rowSize);
                }
            }
            else if (af == AF_INET6)
            {
                int rowSize = Marshal.SizeOf<MIB_TCP6ROW_OWNER_PID>();
                for (int i = 0; i < numEntries; i++)
                {
                    var row = Marshal.PtrToStructure<MIB_TCP6ROW_OWNER_PID>(rowPtr);
                    int pid = (int)row.dwOwningPid;
                    if (targetPids == null || targetPids.Contains(pid))
                    {
                        var localIp = new IPAddress(row.ucLocalAddr).ToString();
                        var remoteIp = new IPAddress(row.ucRemoteAddr).ToString();
                        int localPort = (ushort)IPAddress.NetworkToHostOrder((short)row.dwLocalPort);
                        int remotePort = (ushort)IPAddress.NetworkToHostOrder((short)row.dwRemotePort);
                        string state = GetTcpStateString(row.dwState);

                        var (bytesIn, bytesOut) = QueryTcp6Stats(ref row);

                        result.Add(new RawSocketInfo("TCPv6", localIp, localPort, remoteIp, remotePort, state, pid, bytesIn, bytesOut));
                    }
                    rowPtr = IntPtr.Add(rowPtr, rowSize);
                }
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static (ulong BytesIn, ulong BytesOut) QueryTcpStats(ref MIB_TCPROW_OWNER_PID ownerRow)
    {
        var row = new MIB_TCPROW
        {
            dwState = ownerRow.dwState,
            dwLocalAddr = ownerRow.dwLocalAddr,
            dwLocalPort = ownerRow.dwLocalPort,
            dwRemoteAddr = ownerRow.dwRemoteAddr,
            dwRemotePort = ownerRow.dwRemotePort
        };

        int rodSize = Marshal.SizeOf<TCP_ESTATS_DATA_ROD_v0>();
        IntPtr rodPtr = Marshal.AllocHGlobal(rodSize);

        try
        {
            byte[] zeroBuffer = new byte[rodSize];
            Marshal.Copy(zeroBuffer, 0, rodPtr, rodSize);

            uint status = GetPerTcpConnectionEStats(ref row, TCP_ESTATS_TYPE.TcpConnectionEstatsData,
                IntPtr.Zero, 0, 0,
                IntPtr.Zero, 0, 0,
                rodPtr, 0, (uint)rodSize);

            if (status != 0)
            {
                var rw = new TCP_ESTATS_DATA_RW_v0 { EnableCollection = true };
                uint setStatus = SetPerTcpConnectionEStats(ref row, TCP_ESTATS_TYPE.TcpConnectionEstatsData,
                    ref rw, 0, (uint)Marshal.SizeOf<TCP_ESTATS_DATA_RW_v0>(), 0);

                if (setStatus == 0)
                {
                    Marshal.Copy(zeroBuffer, 0, rodPtr, rodSize);
                    status = GetPerTcpConnectionEStats(ref row, TCP_ESTATS_TYPE.TcpConnectionEstatsData,
                        IntPtr.Zero, 0, 0,
                        IntPtr.Zero, 0, 0,
                        rodPtr, 0, (uint)rodSize);
                }
            }

            if (status == 0)
            {
                var rod = Marshal.PtrToStructure<TCP_ESTATS_DATA_ROD_v0>(rodPtr);
                if (rod.DataBytesIn < 1_000_000_000_000UL && rod.DataBytesOut < 1_000_000_000_000UL)
                {
                    return (rod.DataBytesIn, rod.DataBytesOut);
                }
            }
        }
        catch { }
        finally
        {
            Marshal.FreeHGlobal(rodPtr);
        }

        return (0, 0);
    }

    private static (ulong BytesIn, ulong BytesOut) QueryTcp6Stats(ref MIB_TCP6ROW_OWNER_PID ownerRow)
    {
        var row = new MIB_TCP6ROW
        {
            LocalAddr = new IN6_ADDR { uc = ownerRow.ucLocalAddr },
            dwLocalScopeId = ownerRow.dwLocalScopeId,
            dwLocalPort = ownerRow.dwLocalPort,
            RemoteAddr = new IN6_ADDR { uc = ownerRow.ucRemoteAddr },
            dwRemoteScopeId = ownerRow.dwRemoteScopeId,
            dwRemotePort = ownerRow.dwRemotePort,
            State = ownerRow.dwState
        };

        int rodSize = Marshal.SizeOf<TCP_ESTATS_DATA_ROD_v0>();
        IntPtr rodPtr = Marshal.AllocHGlobal(rodSize);

        try
        {
            byte[] zeroBuffer = new byte[rodSize];
            Marshal.Copy(zeroBuffer, 0, rodPtr, rodSize);

            uint status = GetPerTcp6ConnectionEStats(ref row, TCP_ESTATS_TYPE.TcpConnectionEstatsData,
                IntPtr.Zero, 0, 0,
                IntPtr.Zero, 0, 0,
                rodPtr, 0, (uint)rodSize);

            if (status != 0)
            {
                var rw = new TCP_ESTATS_DATA_RW_v0 { EnableCollection = true };
                uint setStatus = SetPerTcp6ConnectionEStats(ref row, TCP_ESTATS_TYPE.TcpConnectionEstatsData,
                    ref rw, 0, (uint)Marshal.SizeOf<TCP_ESTATS_DATA_RW_v0>(), 0);

                if (setStatus == 0)
                {
                    Marshal.Copy(zeroBuffer, 0, rodPtr, rodSize);
                    status = GetPerTcp6ConnectionEStats(ref row, TCP_ESTATS_TYPE.TcpConnectionEstatsData,
                        IntPtr.Zero, 0, 0,
                        IntPtr.Zero, 0, 0,
                        rodPtr, 0, (uint)rodSize);
                }
            }

            if (status == 0)
            {
                var rod = Marshal.PtrToStructure<TCP_ESTATS_DATA_ROD_v0>(rodPtr);
                if (rod.DataBytesIn < 1_000_000_000_000UL && rod.DataBytesOut < 1_000_000_000_000UL)
                {
                    return (rod.DataBytesIn, rod.DataBytesOut);
                }
            }
        }
        catch { }
        finally
        {
            Marshal.FreeHGlobal(rodPtr);
        }

        return (0, 0);
    }

    private static void GetUdpConnections(uint af, HashSet<int>? targetPids, List<RawSocketInfo> result)
    {
        int size = 0;
        uint status = GetExtendedUdpTable(IntPtr.Zero, ref size, true, af, UDP_TABLE_CLASS.UDP_TABLE_OWNER_PID, 0);
        if (size == 0) return;

        IntPtr buffer = Marshal.AllocHGlobal(size);
        try
        {
            status = GetExtendedUdpTable(buffer, ref size, true, af, UDP_TABLE_CLASS.UDP_TABLE_OWNER_PID, 0);
            if (status != 0) return;

            int numEntries = Marshal.ReadInt32(buffer);
            IntPtr rowPtr = IntPtr.Add(buffer, 4);

            if (af == AF_INET)
            {
                int rowSize = Marshal.SizeOf<MIB_UDPROW_OWNER_PID>();
                for (int i = 0; i < numEntries; i++)
                {
                    var row = Marshal.PtrToStructure<MIB_UDPROW_OWNER_PID>(rowPtr);
                    int pid = (int)row.dwOwningPid;
                    if (targetPids == null || targetPids.Contains(pid))
                    {
                        var localIp = new IPAddress(row.dwLocalAddr).ToString();
                        int localPort = (ushort)IPAddress.NetworkToHostOrder((short)row.dwLocalPort);

                        result.Add(new RawSocketInfo("UDP", localIp, localPort, "*", 0, "LISTENING", pid));
                    }
                    rowPtr = IntPtr.Add(rowPtr, rowSize);
                }
            }
            else if (af == AF_INET6)
            {
                int rowSize = Marshal.SizeOf<MIB_UDP6ROW_OWNER_PID>();
                for (int i = 0; i < numEntries; i++)
                {
                    var row = Marshal.PtrToStructure<MIB_UDP6ROW_OWNER_PID>(rowPtr);
                    int pid = (int)row.dwOwningPid;
                    if (targetPids == null || targetPids.Contains(pid))
                    {
                        var localIp = new IPAddress(row.ucLocalAddr).ToString();
                        int localPort = (ushort)IPAddress.NetworkToHostOrder((short)row.dwLocalPort);

                        result.Add(new RawSocketInfo("UDPv6", localIp, localPort, "*", 0, "LISTENING", pid));
                    }
                    rowPtr = IntPtr.Add(rowPtr, rowSize);
                }
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static string GetTcpStateString(uint state) => state switch
    {
        1 => "CLOSED",
        2 => "LISTENING",
        3 => "SYN_SENT",
        4 => "SYN_RCVD",
        5 => "ESTABLISHED",
        6 => "FIN_WAIT_1",
        7 => "FIN_WAIT_2",
        8 => "CLOSE_WAIT",
        9 => "CLOSING",
        10 => "LAST_ACK",
        11 => "TIME_WAIT",
        12 => "DELETE_TCB",
        _ => "UNKNOWN"
    };

    private const uint AF_INET = 2;
    private const uint AF_INET6 = 23;

    private enum TCP_TABLE_CLASS
    {
        TCP_TABLE_BASIC_LISTENER,
        TCP_TABLE_BASIC_CONNECTIONS,
        TCP_TABLE_BASIC_ALL,
        TCP_TABLE_OWNER_PID_LISTENER,
        TCP_TABLE_OWNER_PID_CONNECTIONS,
        TCP_TABLE_OWNER_PID_ALL,
        TCP_TABLE_OWNER_MODULE_LISTENER,
        TCP_TABLE_OWNER_MODULE_CONNECTIONS,
        TCP_TABLE_OWNER_MODULE_ALL
    }

    private enum UDP_TABLE_CLASS
    {
        UDP_TABLE_BASIC,
        UDP_TABLE_OWNER_PID,
        UDP_TABLE_OWNER_MODULE
    }

    public enum TCP_ESTATS_TYPE
    {
        TcpConnectionEstatsSynOpts,
        TcpConnectionEstatsData,
        TcpConnectionEstatsSndCong,
        TcpConnectionEstatsPath,
        TcpConnectionEstatsSendBuff,
        TcpConnectionEstatsRecvBuff,
        TcpConnectionEstatsObsRecv,
        TcpConnectionEstatsBandwidth,
        TcpConnectionEstatsFineRtt,
        TcpConnectionEstatsMaximum
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TCP_ESTATS_DATA_ROD_v0
    {
        public ulong DataBytesOut;
        public ulong DataSegsOut;
        public ulong DataBytesIn;
        public ulong DataSegsIn;
        public ulong SegsOut;
        public ulong SegsIn;
        public uint SoftErrors;
        public uint SoftErrorReason;
        public uint SndUna;
        public uint SndNxt;
        public uint SndMax;
        public ulong ThruBytesAcked;
        public uint RcvNxt;
        public ulong ThruBytesReceived;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TCP_ESTATS_DATA_RW_v0
    {
        [MarshalAs(UnmanagedType.Bool)]
        public bool EnableCollection;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MIB_TCPROW
    {
        public uint dwState;
        public uint dwLocalAddr;
        public uint dwLocalPort;
        public uint dwRemoteAddr;
        public uint dwRemotePort;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IN6_ADDR
    {
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)]
        public byte[] uc;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MIB_TCP6ROW
    {
        public IN6_ADDR LocalAddr;
        public uint dwLocalScopeId;
        public uint dwLocalPort;
        public IN6_ADDR RemoteAddr;
        public uint dwRemoteScopeId;
        public uint dwRemotePort;
        public uint State;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MIB_TCPROW_OWNER_PID
    {
        public uint dwState;
        public uint dwLocalAddr;
        public uint dwLocalPort;
        public uint dwRemoteAddr;
        public uint dwRemotePort;
        public uint dwOwningPid;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MIB_TCP6ROW_OWNER_PID
    {
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)]
        public byte[] ucLocalAddr;
        public uint dwLocalScopeId;
        public uint dwLocalPort;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)]
        public byte[] ucRemoteAddr;
        public uint dwRemoteScopeId;
        public uint dwRemotePort;
        public uint dwState;
        public uint dwOwningPid;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MIB_UDPROW_OWNER_PID
    {
        public uint dwLocalAddr;
        public uint dwLocalPort;
        public uint dwOwningPid;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MIB_UDP6ROW_OWNER_PID
    {
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)]
        public byte[] ucLocalAddr;
        public uint dwLocalScopeId;
        public uint dwLocalPort;
        public uint dwOwningPid;
    }

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedTcpTable(IntPtr pTcpTable, ref int pdwSize, bool bOrder, uint ulAf, TCP_TABLE_CLASS TableClass, uint Reserved);

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedUdpTable(IntPtr pUdpTable, ref int pdwSize, bool bOrder, uint ulAf, UDP_TABLE_CLASS TableClass, uint Reserved);

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetBestInterface(uint dwDestAddr, out uint pdwBestIfIndex);

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetPerTcpConnectionEStats(
        ref MIB_TCPROW Row,
        TCP_ESTATS_TYPE EstatsType,
        IntPtr Rw,
        uint RwVersion,
        uint RwSize,
        IntPtr Ros,
        uint RosVersion,
        uint RosSize,
        IntPtr Rod,
        uint RodVersion,
        uint RodSize);

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint SetPerTcpConnectionEStats(
        ref MIB_TCPROW Row,
        TCP_ESTATS_TYPE EstatsType,
        ref TCP_ESTATS_DATA_RW_v0 Rw,
        uint RwVersion,
        uint RwSize,
        uint Offset);

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetPerTcp6ConnectionEStats(
        ref MIB_TCP6ROW Row,
        TCP_ESTATS_TYPE EstatsType,
        IntPtr Rw,
        uint RwVersion,
        uint RwSize,
        IntPtr Ros,
        uint RosVersion,
        uint RosSize,
        IntPtr Rod,
        uint RodVersion,
        uint RodSize);

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint SetPerTcp6ConnectionEStats(
        ref MIB_TCP6ROW Row,
        TCP_ESTATS_TYPE EstatsType,
        ref TCP_ESTATS_DATA_RW_v0 Rw,
        uint RwVersion,
        uint RwSize,
        uint Offset);

    #endregion
}

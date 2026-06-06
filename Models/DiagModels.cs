namespace NetFix.Models;

public enum BlockType { None, Throttling, IpBlock, SniBlock, DnsSpoof, MediaThrottle }

public class DcResult
{
    public int DcId { get; set; }
    public string Ip { get; set; } = "";
    public int Port { get; set; }
    public bool Ok { get; set; }
    public double? LatencyMs { get; set; }
    public string Error { get; set; } = "";
    public string LatencyColor => !Ok || LatencyMs is null ? "red"
        : LatencyMs <= 100 ? "green" : LatencyMs <= 200 ? "yellow" : "red";
}

public class PingResult
{
    public string Host { get; set; } = "";
    public bool Ok { get; set; }
    public double? LatencyMs { get; set; }
    public double PacketLoss { get; set; } = 100;
    public string Error { get; set; } = "";
}

public class DnsResult
{
    public string Domain { get; set; } = "";
    public List<string> SystemIps { get; set; } = new();
    public List<string> DohIps { get; set; } = new();
    public bool Spoofed { get; set; }
    public string Error { get; set; } = "";
}

public class DpiResult
{
    public string SniTelegram { get; set; } = "unknown";
    public string SniNeutral { get; set; } = "unknown";
    public bool DpiDetected { get; set; }
    public string Error { get; set; } = "";
}

public class UdpResult
{
    public List<int> TestedPorts { get; set; } = new();
    public List<int> OpenPorts { get; set; } = new();
    public bool Blocked { get; set; } = true;
    public string Error { get; set; } = "";
}

public class MediaThrottleResult
{
    public double SpeedKbps { get; set; }
    public bool Throttled { get; set; }
    public string Error { get; set; } = "";
    public const double SlowThreshold = 200.0;
}

public class AppStatus
{
    public bool TelegramRunning { get; set; }
    public string TelegramProcName { get; set; } = "";
    public bool DiscordRunning { get; set; }
    public string DiscordProcName { get; set; } = "";
    public bool ZapretRunning { get; set; }
    public string ZapretProcName { get; set; } = "";
    public bool GoodbyeDpiRunning { get; set; }
    public string GoodbyeDpiProcName { get; set; } = "";
    public bool WarpRunning { get; set; }
    public string WarpProcName { get; set; } = "";
    public bool TgWsProxyRunning { get; set; }
    public string TgWsProxyProcName { get; set; } = "";
}

public class DiscordPingResult
{
    public string Label { get; set; } = "";
    public string Ip { get; set; } = "";
    public bool Ok { get; set; }
    public double? LatencyMs { get; set; }
    public string Error { get; set; } = "";
}

public class DiagReport
{
    public List<DcResult> DcResults { get; set; } = new();
    public List<PingResult> PingResults { get; set; } = new();
    public DnsResult? DnsResult { get; set; }
    public DpiResult? DpiResult { get; set; }
    public UdpResult? UdpResult { get; set; }
    public MediaThrottleResult? MediaResult { get; set; }
    public AppStatus? AppStatus { get; set; }
    public List<BlockType> BlockTypes { get; set; } = new();
    public List<string> Recommendations { get; set; } = new();
    public List<DiscordPingResult> DiscordPing { get; set; } = new();
}

public enum FixMode { Full, Fast }

public class AppSettings
{
    public string ZapretPath { get; set; } = "";
    public string TgWsProxyPath { get; set; } = "";
    public bool AutostartZapret { get; set; } = false;
    public bool AutostartTgWsProxy { get; set; } = false;
    public bool AutostartApp { get; set; } = false;
    public bool AutoUpdates { get; set; } = true;
    public bool ShowLongCheckDialog { get; set; } = true;
    public bool ShowGameOfferDialog { get; set; } = true;
    public bool DiscordRpcEnabled { get; set; } = true;
    public double GameVolume { get; set; } = 0.5;
    public string FfmpegPath { get; set; } = "";
    public bool DisableComboEffect { get; set; } = false;
    public FixMode Mode { get; set; } = FixMode.Full;
    public string KeyLane0 { get; set; } = "A";
    public string KeyLane1 { get; set; } = "S";
    public string KeyLane2 { get; set; } = "W";
    public string KeyLane3 { get; set; } = "D";
    public List<GameTrackStats> TrackHistory { get; set; } = [];
    public string StatsSortMode { get; set; } = "LastPlayedDesc";
    public string UserSortMode { get; set; } = "DateAddedDesc";
    public string OsuSortMode { get; set; } = "DateAddedDesc";
}

/// <summary>
/// Агрегированная статистика по одному треку. Один объект = один трек, не одна игра.
/// Так история не растёт бесконечно, максимум столько записей сколько уникальных треков.
/// </summary>
public record GameTrackStats
{
    public string TrackTitle { get; init; } = "";
    public int TimesPlayed { get; set; }
    public int BestScore { get; set; }
    public int MinScore { get; set; } = int.MaxValue;
    public double BestAccuracy { get; set; }
    public double WorstAccuracy { get; set; } = 100;
    public int BestCombo { get; set; }
    public string BestRank { get; set; } = "F";
    public int TotalKeyPresses { get; set; }
    public int TotalHits { get; set; }
    public int TotalMisses { get; set; }
    public int TotalNotes { get; set; }
    public DateTime LastPlayed { get; set; }
    public DateTime FirstPlayed { get; set; } = DateTime.Now;
}

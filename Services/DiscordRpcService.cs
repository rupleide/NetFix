using DiscordRPC;
using DiscordRPC.Logging;

namespace NetFix.Services;

public class DiscordRpcService : IDisposable
{
    private DiscordRpcClient? _client;
    private readonly DateTime _startTime = DateTime.UtcNow;
    private int _ping;
    private bool _enabled = true;

    public bool IsPriorityMode { get; set; } = false;
    public bool IsScanning { get; set; } = false;
    public bool UseCleanText { get; set; } = false;

    private const string APP_ID = "1503332623546716240";

    public void Initialize()
    {
        if (!_enabled) return;
        _client = new DiscordRpcClient(APP_ID);
        _client.Logger = new ConsoleLogger { Level = LogLevel.Warning };
        _client.OnReady += (sender, e) => Console.WriteLine($"[Discord] Connected: {e.User.Username}");
        _client.Initialize();
        SetMainMenu();
    }

    public void Enable()
    {
        if (_enabled) return;
        _enabled = true;
        Initialize();
    }

    public void Disable()
    {
        if (!_enabled) return;
        _enabled = false;
        _client?.ClearPresence();
        _client?.Dispose();
        _client = null;
    }

    public void UpdatePing(int ping) => _ping = ping;

    private string P => _ping > 0 ? $"{_ping}мс" : "—";

    private string Up()
    {
        var t = DateTime.UtcNow - _startTime;
        if (t.TotalHours >= 1) return $"{(int)t.TotalHours}ч {t.Minutes}м";
        if (t.TotalMinutes >= 1) return $"{(int)t.TotalMinutes}м";
        return "только запущен";
    }

    private static Assets Logo() => new()
    {
        LargeImageKey  = "netfix_logo_v2",
        LargeImageText = "NetFix by rupleide"
    };

    private static DiscordRPC.Button[] Btns() =>
    [
        new DiscordRPC.Button { Label = "Открыть GitHub", Url = "https://github.com/rupleide/NetFix" },
        new DiscordRPC.Button { Label = "Telegram канал", Url = "https://t.me/NetFixRuBi" }
    ];

    private void Set(string details, string state, Timestamps? ts = null, bool priority = false)
    {
        if (!_enabled) return;
        if (IsPriorityMode && !priority)
        {
            Console.WriteLine($"[Discord] Skipped non-priority update: {details}");
            return;
        }

        _client?.SetPresence(new RichPresence
        {
            Details    = details,
            State      = state,
            Timestamps = ts ?? new Timestamps(_startTime),
            Assets     = Logo(),
            Buttons    = Btns()
        });
    }

    public void SetMainMenu()
        => Set("Чилит в NetFix", $"Пинг: {P} · Запущен {Up()}");

    public void SetDiagnostics(int percent, int problems)
        => Set($"Сканирование: {percent}%",
               $"Найдено проблем: {problems} · {P}",
               new Timestamps(DateTime.UtcNow), priority: true);

    public void SetFixing()
        => Set("Чинит подключение...",
               $"Пинг: {P} · {Up()}",
               new Timestamps(DateTime.UtcNow), priority: true);

    public void SetAllGood(bool zapret, bool tgws)
        => Set(UseCleanText ? $"Чилит в NetFix с пингом {P}" : $"Ёбет в жопу РКН с пингом {P}", "NetFix работает");

    public void SetProblems(string what)
        => Set(what, $"Запущена диагностика · {P}");

    public void SetGamePlaying(string track, int combo, int accuracy, DateTime gameStart)
        => Set($"♪ {track}",
               $"Комбо: {combo}x · Точность: {accuracy}%",
               new Timestamps(gameStart), priority: true);

    public void SetGameResults(string track, string rank, int score, int accuracy, int maxCombo)
        => Set($"Ранг {rank} · {score:N0} очков",
               $"Точность: {accuracy}% · Макс. комбо: {maxCombo}x · {track}",
               priority: true);

    public void SetLevelEditor(string track, int noteCount, DateTime start)
        => Set($"Создаёт уровень: {track}",
               $"Нот записано: {noteCount}",
               new Timestamps(start), priority: true);

    public void Dispose()
    {
        _client?.ClearPresence();
        _client?.Dispose();
    }
}

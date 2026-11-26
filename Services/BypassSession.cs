using NetFix.Models;

namespace NetFix.Services;

public static class BypassSession
{
    public static Func<AppSettings>? ReadSettings { get; set; }
    public static Action<BypassEngine?>? EngineChanged { get; set; }

    public static AppSettings LoadSettings() => ReadSettings?.Invoke() ?? SettingsService.Load();

    public static void Publish(bool starting, BypassEngine engine)
    {
        var callback = EngineChanged;
        if (callback is null)
            return;

        void Apply() => callback(starting ? engine : null);
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
            Apply();
        else
            dispatcher.Invoke(Apply);
    }
}

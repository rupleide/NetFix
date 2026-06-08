using System;
using System.IO;
using System.Text.Json;

namespace NetFix.Services;

public static class TgWsProxySettingsService
{
    private static string ConfigPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "TgWsProxy", "config.json");

    public static bool GetCheckUpdates()
    {
        try
        {
            if (!File.Exists(ConfigPath)) return true;

            var json = File.ReadAllText(ConfigPath);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("check_updates", out var prop))
                return prop.GetBoolean();

            return true;
        }
        catch
        {
            return true;
        }
    }

    public static bool SetCheckUpdates(bool value)
    {
        try
        {
            if (!File.Exists(ConfigPath)) return false;

            var json = File.ReadAllText(ConfigPath);
            using var doc = JsonDocument.Parse(json);
            using var stream = new MemoryStream();
            using var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true });

            writer.WriteStartObject();
            foreach (var el in doc.RootElement.EnumerateObject())
            {
                if (el.NameEquals("check_updates"))
                    writer.WriteBoolean("check_updates", value);
                else
                    el.WriteTo(writer);
            }
            // Если поля не было — добавляем
            if (!doc.RootElement.TryGetProperty("check_updates", out _))
                writer.WriteBoolean("check_updates", value);

            writer.WriteEndObject();
            writer.Flush();

            File.WriteAllBytes(ConfigPath, stream.ToArray());
            return true;
        }
        catch
        {
            return false;
        }
    }

}

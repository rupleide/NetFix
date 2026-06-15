using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using NetFix.Models;

namespace NetFix.Services.Mods
{
    public static class AutoHostsModService
    {
        private static readonly string AppDir = AppContext.BaseDirectory;
        private static readonly string HostsDir = Path.Combine(AppDir, "Mods", "hosts");

        private static readonly string MalwDir = Path.Combine(HostsDir, "dns_malw_link");
        private static readonly string GeoHideDir = Path.Combine(HostsDir, "geo_hide");

        private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(10) };

        public static void EnsureAutoMods()
        {
            try
            {
                Directory.CreateDirectory(HostsDir);
                Directory.CreateDirectory(MalwDir);
                Directory.CreateDirectory(GeoHideDir);

                EnsureModMeta(MalwDir, new ModMeta(
                    Name: "dns.malw.link (Авто)",
                    Author: "ImMALWARE",
                    Version: "1.0",
                    Description: "Автоматически обновляемый список hosts. Источник: dns.malw.link. Последнее обновление: загрузка...",
                    Type: "hosts",
                    RequiredBuild: "",
                    SourceFileName: "list.txt",
                    TargetFile: "hosts"
                ));

                EnsureModMeta(GeoHideDir, new ModMeta(
                    Name: "GeoHide (Авто)",
                    Author: "GeoHide",
                    Version: "1.0",
                    Description: "Автоматически обновляемый список hosts. Источник: dns.geohide.ru. Последнее обновление: загрузка...",
                    Type: "hosts",
                    RequiredBuild: "",
                    SourceFileName: "list.txt",
                    TargetFile: "hosts"
                ));
            }
            catch
            {
            }
        }

        private static void EnsureModMeta(string folder, ModMeta meta)
        {
            var metaPath = Path.Combine(folder, "mod.json");
            var listPath = Path.Combine(folder, "list.txt");

            if (!File.Exists(metaPath))
            {
                var json = JsonSerializer.Serialize(meta, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(metaPath, json);
            }

            if (!File.Exists(listPath))
            {
                File.WriteAllText(listPath, "# Ожидание загрузки списка...");
            }
        }

        public static async Task UpdateAutoModsMetadataAsync()
        {
            try
            {
                var html = await _http.GetStringAsync("https://info.dns.malw.link/");
                var match = Regex.Match(html, @"Последнее\s+обновление:\s*([^<]+)", RegexOptions.IgnoreCase);
                if (match.Success)
                {
                    var date = match.Groups[1].Value.Trim();
                    UpdateModDescription(MalwDir, $"Автоматически обновляемый список hosts. Источник: dns.malw.link. Последнее обновление: {date}");
                }
            }
            catch
            {
            }

            try
            {
                var jsonStr = await _http.GetStringAsync("https://dns.geohide.ru:8443/static/version-hosts.json");
                using var doc = JsonDocument.Parse(jsonStr);
                if (doc.RootElement.TryGetProperty("timestamp", out var prop))
                {
                    var date = prop.GetString();
                    if (!string.IsNullOrEmpty(date))
                    {
                        UpdateModDescription(GeoHideDir, $"Автоматически обновляемый список hosts. Источник: dns.geohide.ru. Последнее обновление: {date}");
                    }
                }
            }
            catch
            {
            }
        }

        private static void UpdateModDescription(string folder, string newDescription)
        {
            var metaPath = Path.Combine(folder, "mod.json");
            if (!File.Exists(metaPath)) return;

            try
            {
                var json = File.ReadAllText(metaPath);
                var meta = JsonSerializer.Deserialize<ModMeta>(json);
                if (meta is null) return;

                if (meta.Description == newDescription) return;

                var updated = new ModMeta(
                    Name: meta.Name,
                    Author: meta.Author,
                    Version: meta.Version,
                    Description: newDescription,
                    Type: meta.Type,
                    RequiredBuild: meta.RequiredBuild,
                    SourceFileName: meta.SourceFileName,
                    TargetFile: meta.TargetFile
                );

                var newJson = JsonSerializer.Serialize(updated, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(metaPath, newJson);
            }
            catch
            {
            }
        }

        public static async Task RefreshAutoModFilesAsync()
        {
            try
            {
                var hostsContent = await _http.GetStringAsync("https://raw.githubusercontent.com/ImMALWARE/dns.malw.link/refs/heads/master/hosts");
                if (!string.IsNullOrWhiteSpace(hostsContent))
                    await File.WriteAllTextAsync(Path.Combine(MalwDir, "list.txt"), hostsContent);
            }
            catch { }

            try
            {
                var hostsContent = await _http.GetStringAsync("https://raw.githubusercontent.com/Internet-Helper/GeoHideDNS/refs/heads/main/hosts/hosts");
                if (!string.IsNullOrWhiteSpace(hostsContent))
                    await File.WriteAllTextAsync(Path.Combine(GeoHideDir, "list.txt"), hostsContent);
            }
            catch { }
        }

        public static async Task<bool> DownloadAutoModsFilesAsync(List<ModEntry> activeMods)
        {
            bool allOk = true;

            foreach (var mod in activeMods)
            {
                if (mod.Type != ModType.Hosts) continue;

                var folderName = Path.GetFileName(mod.FolderPath);

                if (folderName == "dns_malw_link")
                {
                    try
                    {
                        var hostsContent = await _http.GetStringAsync("https://raw.githubusercontent.com/ImMALWARE/dns.malw.link/refs/heads/master/hosts");
                        if (!string.IsNullOrWhiteSpace(hostsContent))
                        {
                            var listPath = Path.Combine(mod.FolderPath, "list.txt");
                            await File.WriteAllTextAsync(listPath, hostsContent);
                        }
                    }
                    catch
                    {
                        allOk = false;
                    }
                }
                else if (folderName == "geo_hide")
                {
                    try
                    {
                        var hostsContent = await _http.GetStringAsync("https://raw.githubusercontent.com/Internet-Helper/GeoHideDNS/refs/heads/main/hosts/hosts");
                        if (!string.IsNullOrWhiteSpace(hostsContent))
                        {
                            var listPath = Path.Combine(mod.FolderPath, "list.txt");
                            await File.WriteAllTextAsync(listPath, hostsContent);
                        }
                    }
                    catch
                    {
                        allOk = false;
                    }
                }
            }

            return allOk;
        }
    }
}

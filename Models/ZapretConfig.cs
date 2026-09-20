using System.Collections.Generic;
using System.Linq;

namespace NetFix.Models;

public class ZapretConfig
{
    public string Name { get; set; } = "";
    public string? CustomName { get; set; } = null;
    public string DisplayName => !string.IsNullOrWhiteSpace(CustomName) ? CustomName : Name;
    public Dictionary<string, ServiceTestResult> Tests { get; set; } = new();
    public int SuccessCount { get; set; }
    public int ErrorCount { get; set; }
    public int AveragePing { get; set; }
    public bool IsValid { get; set; }
    public bool IsPartiallyUsable => !IsValid && SuccessCount > 0;
    public bool IsFromMod { get; set; } = false;
    public string? ModName { get; set; } = null;
}

public class ServiceTestResult
{
    public string ServiceName { get; set; } = "";
    public string HttpStatus { get; set; } = "";
    public string Tls12Status { get; set; } = "";
    public string Tls13Status { get; set; } = "";
    public int Ping { get; set; }

    public bool IsSuccess =>
        HttpStatus == "OK" &&
        (Tls12Status == "OK" || Tls13Status == "OK");
}

public class ZapretConfigCache
{
    public string LastTested { get; set; } = "";
    public string CurrentConfig { get; set; } = "";
    public List<ZapretConfig> ValidConfigs { get; set; } = [];
    public List<ZapretConfig> PartialConfigs { get; set; } = [];
    private List<string> _favoriteConfigs = [];
    public List<string> FavoriteConfigs
    {
        get => _favoriteConfigs ??= [];
        set => _favoriteConfigs = value ?? [];
    }

    public bool HasAnyConfigs => ValidConfigs.Count > 0 || PartialConfigs.Count > 0;

    public string GetDisplayName(string configName)
    {
        var cfg = ValidConfigs.FirstOrDefault(c => c.Name == configName)
               ?? PartialConfigs.FirstOrDefault(c => c.Name == configName);
        return cfg?.DisplayName ?? configName;
    }

    public List<ZapretConfig> GetSelectableConfigs()
    {
        var list = new List<ZapretConfig>();
        list.AddRange(ValidConfigs.OrderBy(c => c.AveragePing));
        list.AddRange(PartialConfigs.OrderByDescending(c => c.SuccessCount).ThenBy(c => c.AveragePing));

        if (FavoriteConfigs is { Count: > 0 })
        {
            return list
                .OrderByDescending(c => FavoriteConfigs.Contains(c.Name))
                .ToList();
        }

        return list;
    }
}

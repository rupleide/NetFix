using System.Text.Json.Serialization;
using Color = System.Windows.Media.Color;

namespace NetFix.Models;

public enum ModType { Strategy, List, Build, Hosts }

public record ModEntry(
    string Name,
    string Author,
    string Version,
    string Description,
    ModType Type,
    string FolderPath,
    string? RequiredBuild,
    string? SourceFileName = null,
    string? TargetFile = null
)
{
    public bool IsActive { get; set; } = false;
    public int SortOrder { get; set; } = 0;
    [JsonIgnore] public bool HasSourceFileName => !string.IsNullOrEmpty(SourceFileName);
    [JsonIgnore] public bool IsAutoMod => System.IO.Path.GetFileName(FolderPath) is "dns_malw_link" or "geo_hide";

    public Color AccentColor => GetAccentColor(Name);

    private static readonly Color[] _palette =
    [
        Color.FromRgb(59, 130, 246),
        Color.FromRgb(139, 92, 246),
        Color.FromRgb(236, 72, 153),
        Color.FromRgb(245, 158, 11),
        Color.FromRgb(16, 185, 129),
        Color.FromRgb(6, 182, 212),
        Color.FromRgb(249, 115, 22),
        Color.FromRgb(168, 85, 247),
    ];

    private static Color GetAccentColor(string name)
    {
        int hash = 17;
        foreach (char c in name) hash = hash * 31 + c;
        return _palette[(hash & 0x7FFFFFFF) % _palette.Length];
    }
}

public record ImportResult(int Added, int Skipped, string? Error);

public record ModMeta(
    string Name,
    string Author,
    string Version,
    string Description,
    string Type,
    string RequiredBuild,
    string? SourceFileName = null,
    string? TargetFile = null
);

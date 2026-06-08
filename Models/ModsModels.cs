using Color = System.Windows.Media.Color;

namespace NetFix.Models;

public enum ModType { Strategy, List, Build }

public record ModEntry(
    string Name,
    string Author,
    string Version,
    string Description,
    ModType Type,
    string FolderPath,
    string? RequiredBuild
)
{
    public bool IsActive { get; set; } = false;
    public int SortOrder { get; set; } = 0;

    public Color AccentColor => GetAccentColor(Name);

    private static readonly Color[] _palette =
    [
        Color.FromRgb(59, 130, 246),   // синий
        Color.FromRgb(139, 92, 246),   // фиолетовый
        Color.FromRgb(236, 72, 153),   // розовый
        Color.FromRgb(245, 158, 11),   // янтарный
        Color.FromRgb(16, 185, 129),   // зелёный
        Color.FromRgb(6, 182, 212),    // циан
        Color.FromRgb(249, 115, 22),   // оранжевый
        Color.FromRgb(168, 85, 247),   // пурпурный
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
    string RequiredBuild
);

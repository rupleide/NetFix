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

public record FileItem(string Name, string Path);

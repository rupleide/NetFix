using System.Text.Json.Serialization;
using System.Windows.Controls;

namespace NetFix.Models;

public class NoteMap
{
    public string? Title { get; set; }
    public string? Author { get; set; }
    public string? TrackFile { get; set; }
    public double Bpm { get; set; } = 140;
    public List<NoteEntry> Notes { get; set; } = new();

    public int NoteCount => Notes.Count;

    [JsonIgnore]
    public string? LevelDir { get; set; }
}

public class NoteEntry
{
    public double Time { get; set; }
    public int Lane { get; set; }

    [JsonIgnore]
    public bool Hit { get; set; }

    [JsonIgnore]
    public Border? Visual { get; set; }

    [JsonIgnore]
    public System.Windows.Media.Effects.DropShadowEffect? Effect { get; set; }
}

using System.Text.Json.Serialization;
using System.Windows.Controls;
using System.Windows.Media.Effects;

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

    [JsonIgnore]
    public string? SourceOszPath { get; set; }
}

public class NoteEntry
{
    public double Time { get; set; }
    public int Lane { get; set; }
    public bool IsHold { get; set; } = false;
    public double HoldEnd { get; set; } = 0;

    [JsonIgnore]
    public bool Hit { get; set; }

    [JsonIgnore]
    public bool HoldActive { get; set; }

    [JsonIgnore]
    public bool HoldCompleted { get; set; }

    [JsonIgnore]
    public Border? Visual { get; set; }

    [JsonIgnore]
    public Border? HoldBody { get; set; }

    [JsonIgnore]
    public DropShadowEffect? Effect { get; set; }
}

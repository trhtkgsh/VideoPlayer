using System.IO;
using System.Text.Json;

namespace VideoPlayer.Services;

/// <summary>Per-file memory: last playback position and when the file was last opened.</summary>
public sealed class FileRecord
{
    public string Path { get; set; } = "";

    public long PositionMs { get; set; }

    public long LastOpenedTicks { get; set; }
}

/// <summary>Settings and per-file records, saved as JSON in the user's local app data folder.</summary>
public sealed class AppState
{
    private const int MaxRecords = 300;

    private static readonly string StatePath = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VideoPlayer", "state.json");

    public bool AskDefaultOnStartup { get; set; } = true;

    /// <summary>Font family used for subtitles. Must contain Hebrew glyphs for Hebrew text.</summary>
    public string SubtitleFont { get; set; } = "Arial";

    public int SubtitleSize { get; set; } = 24;

    /// <summary>Subtitle text color as an RGB value (0xRRGGBB).</summary>
    public int SubtitleColor { get; set; } = 0xFFFFFF;

    /// <summary>"auto" lets VLC detect the encoding; otherwise a charset name such as UTF-8 or WINDOWS-1255.</summary>
    public string SubtitleEncoding { get; set; } = "auto";

    public PostProcessingSettings PostProcessing { get; set; } = new();

    public List<FileRecord> Files { get; set; } = [];

    public static AppState Load()
    {
        try
        {
            if (File.Exists(StatePath))
            {
                var state = JsonSerializer.Deserialize<AppState>(File.ReadAllText(StatePath));
                if (state is not null)
                {
                    state.Files.RemoveAll(r => string.IsNullOrEmpty(r.Path));
                    return state;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            // A damaged state file must not stop the player from starting; it is replaced on the next save.
        }

        return new AppState();
    }

    public void Save()
    {
        Files.RemoveAll(r => r.PositionMs == 0 && r.LastOpenedTicks == 0);
        Files = Files.OrderByDescending(r => r.LastOpenedTicks).Take(MaxRecords).ToList();

        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(StatePath)!);
        var temp = StatePath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temp, StatePath, true);
    }

    public FileRecord? Find(string path) =>
        Files.FirstOrDefault(r => string.Equals(r.Path, path, StringComparison.OrdinalIgnoreCase));

    public void SetPosition(string path, long positionMs) => GetOrAdd(path).PositionMs = positionMs;

    public void Touch(string path)
    {
        GetOrAdd(path).LastOpenedTicks = DateTime.UtcNow.Ticks;
    }

    /// <summary>Files opened recently that still exist on disk, newest first.</summary>
    public IEnumerable<FileRecord> Recent(int count) =>
        Files.Where(r => r.LastOpenedTicks > 0 && File.Exists(r.Path))
             .OrderByDescending(r => r.LastOpenedTicks)
             .Take(count);

    /// <summary>Clears the recent list only; saved resume positions are kept.</summary>
    public void ClearRecent()
    {
        foreach (var record in Files)
        {
            record.LastOpenedTicks = 0;
        }
    }

    private FileRecord GetOrAdd(string path)
    {
        var record = Find(path);
        if (record is null)
        {
            record = new FileRecord { Path = path };
            Files.Add(record);
        }

        return record;
    }
}

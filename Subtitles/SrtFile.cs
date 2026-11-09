using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace VideoPlayer.Subtitles;

public static partial class SrtFile
{
    [GeneratedRegex(@"^(\d{1,2}):(\d{1,2}):(\d{1,2})[,.](\d{1,3})$")]
    private static partial Regex TimeFormat();

    public static List<SubtitleCue> Load(string path)
    {
        var lines = File.ReadAllLines(path);
        var cues = new List<SubtitleCue>();

        for (var i = 0; i < lines.Length; i++)
        {
            if (!lines[i].Contains("-->"))
            {
                continue;
            }

            var parts = lines[i].Split("-->");
            var cue = new SubtitleCue
            {
                StartText = parts[0].Trim().Replace('.', ','),
                EndText = parts[1].Trim().Split(' ')[0].Replace('.', ','),
            };

            var textLines = new List<string>();
            var j = i + 1;
            for (; j < lines.Length && lines[j].Trim().Length > 0; j++)
            {
                textLines.Add(lines[j].TrimEnd());
            }

            cue.Text = string.Join('\n', textLines);
            cues.Add(cue);
            i = j;
        }

        return cues;
    }

    public static bool TrySave(string path, IEnumerable<SubtitleCue> cues, out string? error)
    {
        var parsed = new List<(TimeSpan Start, TimeSpan End, string Text)>();
        var row = 0;

        foreach (var cue in cues)
        {
            row++;
            if (!TryParseTime(cue.StartText, out var start) || !TryParseTime(cue.EndText, out var end))
            {
                error = $"שורה {row}: זמן לא תקין (פורמט נדרש: 00:00:01,500).";
                return false;
            }

            if (end < start)
            {
                error = $"שורה {row}: זמן הסיום קודם לזמן ההתחלה.";
                return false;
            }

            parsed.Add((start, end, cue.Text));
        }

        var sb = new StringBuilder();
        var index = 1;
        foreach (var (start, end, text) in parsed.OrderBy(p => p.Start))
        {
            sb.Append(index++).Append('\n');
            sb.Append(FormatTime(start)).Append(" --> ").Append(FormatTime(end)).Append('\n');
            sb.Append(text).Append("\n\n");
        }

        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
        error = null;
        return true;
    }

    public static void Shift(IEnumerable<SubtitleCue> cues, TimeSpan offset)
    {
        foreach (var cue in cues)
        {
            if (TryParseTime(cue.StartText, out var start))
            {
                cue.StartText = FormatTime(ClampToZero(start + offset));
            }

            if (TryParseTime(cue.EndText, out var end))
            {
                cue.EndText = FormatTime(ClampToZero(end + offset));
            }
        }
    }

    public static bool TryParseTime(string? text, out TimeSpan time)
    {
        var match = TimeFormat().Match(text?.Trim() ?? string.Empty);
        if (!match.Success)
        {
            time = TimeSpan.Zero;
            return false;
        }

        var hours = int.Parse(match.Groups[1].Value);
        var minutes = int.Parse(match.Groups[2].Value);
        var seconds = int.Parse(match.Groups[3].Value);
        if (minutes >= 60 || seconds >= 60)
        {
            time = TimeSpan.Zero;
            return false;
        }

        var milliseconds = int.Parse(match.Groups[4].Value.PadRight(3, '0'));
        time = TimeSpan.FromHours(hours) + TimeSpan.FromMinutes(minutes)
               + TimeSpan.FromSeconds(seconds) + TimeSpan.FromMilliseconds(milliseconds);
        return true;
    }

    public static string FormatTime(TimeSpan time) =>
        $"{(int)time.TotalHours:00}:{time.Minutes:00}:{time.Seconds:00},{time.Milliseconds:000}";

    private static TimeSpan ClampToZero(TimeSpan time) => time < TimeSpan.Zero ? TimeSpan.Zero : time;
}

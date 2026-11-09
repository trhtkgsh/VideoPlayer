namespace VideoPlayer.Subtitles;

public sealed class SubtitleCue
{
    public string StartText { get; set; } = "00:00:00,000";

    public string EndText { get; set; } = "00:00:00,000";

    public string Text { get; set; } = string.Empty;

    // The grid shows multi-line text on one row, so line breaks are displayed as ⏎.
    public string DisplayText
    {
        get => string.Join(" ⏎ ", Text.Split('\n'));
        set => Text = string.Join('\n', value.Split('⏎').Select(s => s.Trim()));
    }
}

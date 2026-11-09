using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using VideoPlayer.Subtitles;

namespace VideoPlayer;

public partial class SubtitleEditorWindow : Window
{
    private readonly string _path;
    private readonly ObservableCollection<SubtitleCue> _cues;

    public bool Saved { get; private set; }

    public SubtitleEditorWindow(string path)
    {
        InitializeComponent();
        _path = path;
        _cues = new ObservableCollection<SubtitleCue>(
            File.Exists(path) ? SrtFile.Load(path) : new List<SubtitleCue>());
        CueGrid.ItemsSource = _cues;
        Title = $"עריכת כתוביות - {System.IO.Path.GetFileName(path)}";
    }

    private void AddCue_Click(object sender, RoutedEventArgs e)
    {
        var last = _cues.LastOrDefault();
        var startMs = last is not null && SrtFile.TryParseTime(last.EndText, out var end)
            ? end.TotalMilliseconds
            : 0;

        var start = TimeSpan.FromMilliseconds(startMs);
        var cue = new SubtitleCue
        {
            StartText = SrtFile.FormatTime(start),
            EndText = SrtFile.FormatTime(start + TimeSpan.FromSeconds(2)),
            Text = string.Empty,
        };
        _cues.Add(cue);
        CueGrid.SelectedItem = cue;
        CueGrid.ScrollIntoView(cue);
    }

    private void RemoveCue_Click(object sender, RoutedEventArgs e)
    {
        if (CueGrid.SelectedItem is SubtitleCue cue)
        {
            _cues.Remove(cue);
        }
    }

    private void Shift_Click(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(ShiftBox.Text, out var ms))
        {
            ErrorText.Text = "ערך הזזה לא תקין. הזן מספר שלם של מילישניות.";
            return;
        }

        SrtFile.Shift(_cues, TimeSpan.FromMilliseconds(ms));
        ErrorText.Text = string.Empty;
        CueGrid.Items.Refresh();
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        CueGrid.CommitEdit(System.Windows.Controls.DataGridEditingUnit.Row, true);

        if (!SrtFile.TrySave(_path, _cues, out var error))
        {
            ErrorText.Text = error;
            return;
        }

        Saved = true;
        Close();
    }
}

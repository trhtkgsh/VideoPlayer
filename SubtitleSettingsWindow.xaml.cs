using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using VideoPlayer.Services;

namespace VideoPlayer;

public partial class SubtitleSettingsWindow : Window
{
    private const char HebrewAlef = '\u05D0';

    private sealed record ChoiceItem(string Value, string Text);

    public string SelectedFont { get; private set; } = "Arial";
    public int SelectedSize { get; private set; } = 24;
    public int SelectedColor { get; private set; } = 0xFFFFFF;
    public string SelectedEncoding { get; private set; } = "auto";

    public SubtitleSettingsWindow(AppState state)
    {
        InitializeComponent();

        FontCombo.ItemsSource = BuildFontChoices(state.SubtitleFont);
        FontCombo.SelectedValue = state.SubtitleFont;

        SizeCombo.ItemsSource = new[] { 16, 20, 24, 28, 32, 40, 48 }
            .Select(s => new ChoiceItem(s.ToString(), s.ToString()))
            .ToList();
        SizeCombo.SelectedValue = state.SubtitleSize.ToString();

        ColorCombo.ItemsSource = new List<ChoiceItem>
        {
            new(0xFFFFFF.ToString(), "לבן"),
            new(0xFFFF00.ToString(), "צהוב"),
            new(0x00FFFF.ToString(), "תכלת"),
            new(0x00FF00.ToString(), "ירוק"),
            new(0xFF8000.ToString(), "כתום"),
        };
        ColorCombo.SelectedValue = state.SubtitleColor.ToString();

        EncodingCombo.ItemsSource = new List<ChoiceItem>
        {
            new("auto", "אוטומטי"),
            new("UTF-8", "UTF-8"),
            new("WINDOWS-1255", "Windows-1255 (עברית)"),
        };
        EncodingCombo.SelectedValue = state.SubtitleEncoding;

        ApplyPreviewFont();
    }

    /// <summary>Lists installed fonts that can draw Hebrew. The current font is always included so it can be kept.</summary>
    private static List<ChoiceItem> BuildFontChoices(string current)
    {
        var names = new SortedSet<string>(StringComparer.CurrentCultureIgnoreCase);
        foreach (var family in Fonts.SystemFontFamilies)
        {
            if (SupportsHebrew(family))
            {
                names.Add(family.Source);
            }
        }

        names.Add(current);
        return names.Select(n => new ChoiceItem(n, n)).ToList();
    }

    private static bool SupportsHebrew(FontFamily family)
    {
        foreach (var typeface in family.GetTypefaces())
        {
            if (typeface.TryGetGlyphTypeface(out var glyphs) && glyphs.CharacterToGlyphMap.ContainsKey(HebrewAlef))
            {
                return true;
            }
        }

        return false;
    }

    private void FontCombo_SelectionChanged(object sender, SelectionChangedEventArgs e) => ApplyPreviewFont();

    private void ColorCombo_SelectionChanged(object sender, SelectionChangedEventArgs e) => ApplyPreviewFont();

    private void ApplyPreviewFont()
    {
        if (FontCombo.SelectedValue is string font)
        {
            PreviewText.FontFamily = new FontFamily(font);
        }

        if (SizeCombo.SelectedValue is string size && int.TryParse(size, out var px))
        {
            PreviewText.FontSize = px;
        }

        if (ColorCombo.SelectedValue is string color && int.TryParse(color, out var rgb))
        {
            PreviewText.Foreground = new SolidColorBrush(Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb));
        }
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        SelectedFont = FontCombo.SelectedValue as string ?? SelectedFont;
        SelectedSize = int.TryParse(SizeCombo.SelectedValue as string, out var size) ? size : SelectedSize;
        SelectedColor = int.TryParse(ColorCombo.SelectedValue as string, out var color) ? color : SelectedColor;
        SelectedEncoding = EncodingCombo.SelectedValue as string ?? SelectedEncoding;
        DialogResult = true;
    }
}

using System.Windows;
using VideoPlayer.Services;

namespace VideoPlayer;

/// <summary>
/// Edits picture and sound adjustments. Each change is passed to <paramref name="preview"/> right away;
/// the caller restores the saved values when the dialog is cancelled.
/// </summary>
public partial class PostProcessingWindow : Window
{
    private readonly PostProcessingSettings _draft;
    private readonly Action<PostProcessingSettings> _preview;

    public PostProcessingWindow(PostProcessingSettings draft, Action<PostProcessingSettings> preview)
    {
        InitializeComponent();
        _draft = draft;
        _preview = preview;

        LoadControls();

        BrightnessSlider.ValueChanged += (_, _) => SyncFromControls();
        ContrastSlider.ValueChanged += (_, _) => SyncFromControls();
        SaturationSlider.ValueChanged += (_, _) => SyncFromControls();
    }

    private void LoadControls()
    {
        BrightnessSlider.Value = _draft.Brightness;
        ContrastSlider.Value = _draft.Contrast;
        SaturationSlider.Value = _draft.Saturation;
        DeinterlaceCheck.IsChecked = _draft.Deinterlace;
        SharpenCheck.IsChecked = _draft.Sharpen;
        NormalizeCheck.IsChecked = _draft.NormalizeAudio;
        UpdateTexts();
    }

    private void SyncFromControls()
    {
        _draft.Brightness = (float)BrightnessSlider.Value;
        _draft.Contrast = (float)ContrastSlider.Value;
        _draft.Saturation = (float)SaturationSlider.Value;
        _draft.Deinterlace = DeinterlaceCheck.IsChecked == true;
        _draft.Sharpen = SharpenCheck.IsChecked == true;
        _draft.NormalizeAudio = NormalizeCheck.IsChecked == true;

        UpdateTexts();
        _preview(_draft);
    }

    private void UpdateTexts()
    {
        BrightnessText.Text = $"{BrightnessSlider.Value * 100:0}%";
        ContrastText.Text = $"{ContrastSlider.Value * 100:0}%";
        SaturationText.Text = $"{SaturationSlider.Value * 100:0}%";
    }

    private void Option_Click(object sender, RoutedEventArgs e) => SyncFromControls();

    private void Reset_Click(object sender, RoutedEventArgs e)
    {
        _draft.CopyFrom(new PostProcessingSettings());
        LoadControls();
        _preview(_draft);
    }

    private void Ok_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}

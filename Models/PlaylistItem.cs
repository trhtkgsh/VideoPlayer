using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using VideoPlayer.Services;

namespace VideoPlayer.Models;

public sealed class PlaylistItem(string filePath) : INotifyPropertyChanged
{
    private static readonly HashSet<string> AudioExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp3", ".flac", ".wav", ".aac", ".m4a", ".ogg", ".opus", ".wma", ".alac", ".aiff"
    };

    private ImageSource? _thumbnail;
    private ImageSource? _icon;
    private bool _iconLoaded;

    private bool _isQueued;

    public event PropertyChangedEventHandler? PropertyChanged;

    public string FilePath { get; } = filePath;

    public string DisplayName { get; } = Path.GetFileName(filePath);

    public bool IsAudio { get; } = AudioExtensions.Contains(Path.GetExtension(filePath));

    /// <summary>True when this explorer entry is already in the playlist.</summary>
    public bool IsQueued
    {
        get => _isQueued;
        set
        {
            if (_isQueued != value)
            {
                _isQueued = value;
                OnPropertyChanged();
            }
        }
    }

    /// <summary>Glyph shown when there is no thumbnail: a music note for audio, a play sign for video.</summary>
    public string FallbackGlyph => IsAudio ? "♫" : "▶";

    /// <summary>Preview image for the playlist. Null until loaded, or when none is available.</summary>
    public ImageSource? Thumbnail
    {
        get => _thumbnail;
        set
        {
            if (!ReferenceEquals(_thumbnail, value))
            {
                _thumbnail = value;
                OnPropertyChanged();
            }
        }
    }

    /// <summary>Shell icon of the file type, used in the explorer.</summary>
    public ImageSource? Icon
    {
        get
        {
            if (!_iconLoaded)
            {
                _iconLoaded = true;
                _icon = ShellImages.GetFileIcon(FilePath);
            }

            return _icon;
        }
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

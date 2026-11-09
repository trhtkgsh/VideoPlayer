using System.Collections.ObjectModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using LibVLCSharp.Shared;
using Microsoft.Win32;
using ImageSource = System.Windows.Media.ImageSource;
using VideoPlayer.Models;
using VideoPlayer.Services;

namespace VideoPlayer;

public partial class MainWindow : Window
{
    private const int WhMouseLl = 14;
    private const int WmLButtonDown = 0x0201;
    private const int WmMouseWheel = 0x020A;
    private const int WmRButtonUp = 0x0205;
    private const int VolumeStep = 5;
    private const int SmCxDoubleClick = 36;
    private const int SmCyDoubleClick = 37;

    private delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MsLLHookStruct
    {
        public NativePoint Pt;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelMouseProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetDoubleClickTime();

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MonitorInfo lpmi);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MonitorInfo
    {
        public int cbSize;
        public int Left, Top, Right, Bottom;
        public int WorkLeft, WorkTop, WorkRight, WorkBottom;
        public uint Flags;
    }

    private static readonly float[] Speeds = [0.25f, 0.5f, 0.75f, 1f, 1.25f, 1.5f, 2f, 3f, 4f];
    private static readonly string[] SubtitleExtensions = [".srt", ".ass", ".ssa", ".vtt"];
    private static readonly HashSet<string> MediaExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".mkv", ".avi", ".mov", ".wmv", ".flv", ".webm", ".m4v", ".mpg", ".mpeg", ".ts", ".m2ts",
        ".3gp", ".ogv", ".vob", ".mp3", ".flac", ".wav", ".aac", ".ogg", ".m4a", ".wma", ".opus", ".aiff",
    };
    private const long RestartThresholdMs = 3000;
    private const long FinishedMarginMs = 5000;
    private const int PositionSaveTickCount = 66; // about 10 seconds at the 150 ms position timer interval
    private const int RecentLimit = 50;

    private readonly LibVLC _libVLC;
    private readonly MediaPlayer _mediaPlayer;
    private readonly ObservableCollection<PlaylistItem> _playlist = [];
    private readonly ObservableCollection<RecentEntry> _recent = [];
    private readonly AppState _state = AppState.Load();
    private string? _currentPath;
    private int _positionSaveTicks;
    private readonly DispatcherTimer _positionTimer;
    private readonly DispatcherTimer _osdTimer;
    // Must stay referenced for the lifetime of the hook so the GC cannot collect the delegate.
    private readonly LowLevelMouseProc _mouseProc;

    private Media? _media;
    private readonly ThumbnailGenerator _thumbs;
    private int _hoverBucket = -1;
    private int _currentIndex = -1;
    private float _rate = 1f;
    private string? _currentSubtitle;
    private string? _pendingSubtitle;
    private long _pendingResumeMs;
    private int _lastSpuId = -1;
    private bool _suppressSeek;
    private bool _isFullscreen;
    private bool _osdAtTopRight;
    private bool _loop;
    private readonly DispatcherTimer _thumbRequestTimer;
    private GridLength[]? _savedColumnWidths;
    private WindowState _windowStateBeforeFullscreen;
    private Rect _normalBounds;
    private ResizeMode _savedResizeMode;
    private const uint MonitorDefaultToNearest = 2;
    private IntPtr _windowHandle;
    private IntPtr _mouseHook;
    private bool _hasLastVideoClick;
    private uint _lastVideoClickTime;
    private NativePoint _lastVideoClickPoint;

    public MainWindow()
    {
        _mouseProc = MouseHookCallback;
        _libVLC = new LibVLC("--avcodec-hw=any", "--no-osd", "--no-video-title-show");
        _mediaPlayer = new MediaPlayer(_libVLC);
        _mediaPlayer.Playing += MediaPlayer_Playing;
        _mediaPlayer.EndReached += MediaPlayer_EndReached;
        _mediaPlayer.EncounteredError += (_, _) => Dispatcher.BeginInvoke(new Action(ShowPlaybackError));
        _thumbs = new ThumbnailGenerator(_libVLC, Dispatcher);
        _thumbs.ThumbnailReady += OnThumbnailReady;

        InitializeComponent();
        OsdPopup.CustomPopupPlacementCallback = PlaceOsd;

        VideoView.MediaPlayer = _mediaPlayer;
        ApplyVideoAdjustments(_state.PostProcessing);
        PlaylistList.ItemsSource = _playlist;
        RecentList.ItemsSource = _recent;
        AskDefaultMenuItem.IsChecked = _state.AskDefaultOnStartup;
        BuildSpeedButtons();

        Loaded += (_, _) =>
        {
            _windowHandle = new WindowInteropHelper(this).Handle;
            _mouseHook = SetWindowsHookEx(WhMouseLl, _mouseProc, GetModuleHandle(null), 0);
            OpenStartupFiles();
            RefreshRecentList();
            CheckDefaultApp();
        };

        _positionTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        _positionTimer.Tick += (_, _) => UpdatePosition();
        _positionTimer.Start();

        _osdTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.2) };
        _osdTimer.Tick += (_, _) =>
        {
            _osdTimer.Stop();
            OsdPopup.IsOpen = false;
        };

        // Thumbnails are requested only after the mouse rests on the seekbar, so scrubbing does not decode frames.
        _thumbRequestTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        _thumbRequestTimer.Tick += (_, _) => RequestHoverThumbnail();

        VolumeSlider.Value = 100;
        LoadDrives();
    }

    // ---------- Playback core ----------

    private void PlayIndex(int index, long resumeMs = 0, string? subtitle = null)
    {
        if (index < 0 || index >= _playlist.Count)
        {
            return;
        }

        var path = _playlist[index].FilePath;
        if (!CanOpenMedia(path, out var problem))
        {
            MessageBox.Show(this, problem, "לא ניתן לנגן את הקובץ", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        RememberPosition();

        _currentIndex = index;
        _currentPath = path;
        _pendingResumeMs = resumeMs > 0 ? resumeMs : _state.Find(path)?.PositionMs ?? 0;
        _currentSubtitle = subtitle ?? FindSidecarSubtitle(path);
        _pendingSubtitle = _currentSubtitle;

        var previous = _media;
        _media = new Media(_libVLC, path, FromType.FromPath);
        ApplySubtitleOptions(_media);
        ApplyMediaFilters(_media, _state.PostProcessing);
        _mediaPlayer.Play(_media);
        previous?.Dispose();

        _state.Touch(path);
        _state.Save();
        RefreshRecentList();

        PlaylistList.SelectedIndex = index;
        EmptyHint.Visibility = Visibility.Collapsed;
        Title = $"{_playlist[index].DisplayName} - נגן וידאו";
    }

    private void PlayIfIdle(int index)
    {
        if (index >= 0 && _media is null)
        {
            PlayIndex(index);
        }
    }

    /// <summary>Checks the file before playback so the user gets a specific reason instead of a generic failure.</summary>
    private static bool CanOpenMedia(string path, out string problem)
    {
        problem = string.Empty;
        if (!File.Exists(path))
        {
            problem = $"הקובץ לא נמצא:\n{path}";
            return false;
        }

        try
        {
            using var stream = File.OpenRead(path);
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            problem = $"אין הרשאת גישה לקובץ:\n{path}";
        }
        catch (IOException)
        {
            problem = $"הקובץ נעול או לא זמין כרגע:\n{path}";
        }

        return false;
    }

    private void ShowPlaybackError()
    {
        var path = _currentPath ?? string.Empty;
        if (!CanOpenMedia(path, out var problem))
        {
            MessageBox.Show(this, problem, "שגיאה בניגון", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        MessageBox.Show(this,
            "הקובץ פגום, בפורמט שאינו נתמך, או שחסר קודק מתאים לפענוח שלו:\n" + path,
            "שגיאה בניגון", MessageBoxButton.OK, MessageBoxImage.Error);
    }

    private void MediaPlayer_Playing(object? sender, EventArgs e) =>
        Dispatcher.BeginInvoke(new Action(() =>
        {
            _mediaPlayer.SetRate(_rate);

            if (_pendingResumeMs > 0)
            {
                _mediaPlayer.Time = _pendingResumeMs;
                ShowOsd($"ממשיך מ-{FormatTime(_pendingResumeMs)}");
                _pendingResumeMs = 0;
            }

            if (_pendingSubtitle is { } subtitle)
            {
                _mediaPlayer.AddSlave(MediaSlaveType.Subtitle, new Uri(subtitle).AbsoluteUri, true);
                _pendingSubtitle = null;
            }
        }));

    private void MediaPlayer_EndReached(object? sender, EventArgs e) =>
        Dispatcher.BeginInvoke(new Action(() =>
        {
            RememberPosition();
            if (_loop && _currentPath is not null)
            {
                _state.SetPosition(_currentPath, 0);
                PlayIndex(_currentIndex);
                return;
            }

            PlayIndex(_currentIndex + 1);
        }));

    private void Loop_Click(object sender, RoutedEventArgs e)
    {
        _loop = !_loop;
        if (sender is MenuItem item)
        {
            item.IsChecked = _loop;
        }

        ShowOsd(_loop ? "לולאה: פעילה" : "לולאה: כבויה");
    }

    private void FrameForward_Click(object sender, RoutedEventArgs e)
    {
        _mediaPlayer.SetPause(true);
        _mediaPlayer.NextFrame();
        ShowOsd("פריים קדימה");
    }

    private void FrameBackward_Click(object sender, RoutedEventArgs e)
    {
        var fps = _mediaPlayer.Fps > 0 ? _mediaPlayer.Fps : 25f;
        _mediaPlayer.SetPause(true);
        _mediaPlayer.Time = Math.Max(0, _mediaPlayer.Time - (long)(1000 / fps));
        ShowOsd("פריים אחורה");
    }

    private void UpdatePosition()
    {
        var length = _mediaPlayer.Length;
        var time = Math.Max(0, _mediaPlayer.Time);

        if (!SeekSlider.IsMouseCaptured && length > 0)
        {
            _suppressSeek = true;
            SeekSlider.Value = time * SeekSlider.Maximum / length;
            ThinProgress.Value = time * ThinProgress.Maximum / length;
            _suppressSeek = false;
        }

        TimeText.Text = $"{FormatTime(time)} / {FormatTime(length)}";
        var playing = _mediaPlayer.IsPlaying;
        PlayPauseText.Visibility = playing ? Visibility.Collapsed : Visibility.Visible;
        PauseBars.Visibility = playing ? Visibility.Visible : Visibility.Collapsed;

        if (playing && ++_positionSaveTicks >= PositionSaveTickCount)
        {
            _positionSaveTicks = 0;
            RememberPosition();
        }
    }

    /// <summary>Stores where the current file stopped so it can resume later. Files played to the end start over.</summary>
    private void RememberPosition()
    {
        var length = _mediaPlayer.Length;
        if (_currentPath is null || _pendingResumeMs > 0 || length <= 0)
        {
            return;
        }

        var time = Math.Max(0, _mediaPlayer.Time);
        _state.SetPosition(_currentPath, time >= length - FinishedMarginMs ? 0 : time);
        _state.Save();
    }

    private void SeekSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppressSeek || _mediaPlayer.Length <= 0)
        {
            return;
        }

        _mediaPlayer.Position = (float)(e.NewValue / SeekSlider.Maximum);
    }

    private void SeekBy(long deltaMs)
    {
        if (_media is null)
        {
            return;
        }

        var length = _mediaPlayer.Length;
        var target = Math.Max(0, _mediaPlayer.Time + deltaMs);
        if (length > 0)
        {
            target = Math.Min(target, length);
        }

        _mediaPlayer.Time = target;
        ShowOsd($"{(deltaMs < 0 ? "אחורה" : "קדימה")} {Math.Abs(deltaMs) / 1000} שניות");
    }

    private void BuildSpeedButtons()
    {
        foreach (var speed in Speeds)
        {
            var button = new ToggleButton
            {
                Content = $"{speed}x",
                Tag = speed,
                MinWidth = 56,
                Margin = new Thickness(2, 0, 2, 0),
                HorizontalContentAlignment = HorizontalAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center,
            };
            button.Click += SpeedButton_Click;
            SpeedPanel.Children.Add(button);
        }

        UpdateSpeedButtons();
    }

    private void SpeedButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is ToggleButton { Tag: float rate })
        {
            ApplySpeed(rate);
        }
    }

    private void UpdateSpeedButtons()
    {
        foreach (var button in SpeedPanel.Children.OfType<ToggleButton>())
        {
            button.IsChecked = button.Tag is float speed && speed == _rate;
        }
    }

    private void ApplySpeed(float rate)
    {
        _rate = rate;
        _mediaPlayer.SetRate(rate);
        UpdateSpeedButtons();
        if (IsLoaded)
        {
            ShowOsd($"מהירות {rate}x");
        }
    }

    private void SetSpeedIndex(int index)
    {
        if (index >= 0 && index < Speeds.Length)
        {
            ApplySpeed(Speeds[index]);
        }
    }

    private void VolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        var volume = (int)Math.Round(e.NewValue);
        _mediaPlayer.Volume = volume;
        VolumeText.Text = $"{volume}%";
    }

    private void Volume_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        ChangeVolume(e.Delta > 0 ? VolumeStep : -VolumeStep);
        e.Handled = true;
    }

    private void ChangeVolume(int step)
    {
        VolumeSlider.Value = Math.Clamp(VolumeSlider.Value + step, VolumeSlider.Minimum, VolumeSlider.Maximum);
        ShowOsd($"עוצמה {(int)VolumeSlider.Value}%", atTopRight: true);
    }

    // ---------- Playlist ----------

    private int AddFiles(IEnumerable<string> files)
    {
        var firstIndex = -1;
        foreach (var file in files)
        {
            if (firstIndex < 0)
            {
                firstIndex = _playlist.Count;
            }

            var item = new PlaylistItem(file);
            _playlist.Add(item);
            ThumbnailLoader.Enqueue(item);
        }

        RefreshQueuedFlags();
        return firstIndex;
    }

    private void RefreshQueuedFlags()
    {
        var queued = _playlist.Select(p => p.FilePath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var item in ExplorerFiles.Items.OfType<PlaylistItem>())
        {
            item.IsQueued = queued.Contains(item.FilePath);
        }
    }

    private static IEnumerable<string> GetMediaFiles(string folder) =>
        Directory.EnumerateFiles(folder)
            .Where(f => MediaExtensions.Contains(Path.GetExtension(f)))
            .OrderBy(f => f, StringComparer.CurrentCultureIgnoreCase);

    private static bool IsFromButton(object source)
    {
        for (var element = source as DependencyObject; element is not null; element = System.Windows.Media.VisualTreeHelper.GetParent(element))
        {
            if (element is ButtonBase)
            {
                return true;
            }
        }

        return false;
    }

    private void PlaylistList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (IsFromButton(e.OriginalSource))
        {
            return;
        }

        if (PlaylistList.SelectedIndex >= 0)
        {
            PlayIndex(PlaylistList.SelectedIndex);
        }
    }

    private void RemoveSelected_Click(object sender, RoutedEventArgs e) =>
        RemovePlaylistItems(PlaylistList.SelectedItems.Cast<PlaylistItem>().ToList());

    private void RemovePlaylistItem_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is PlaylistItem item)
        {
            RemovePlaylistItems([item]);
        }
    }

    private void ToggleQueueItem_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is not PlaylistItem item)
        {
            return;
        }

        if (item.IsQueued)
        {
            RemovePlaylistItems(_playlist.Where(p => p.FilePath == item.FilePath).ToList());
        }
        else
        {
            AddFiles([item.FilePath]);
        }
    }

    private void RemovePlaylistItems(IReadOnlyCollection<PlaylistItem> items)
    {
        var current = _currentIndex >= 0 ? _playlist[_currentIndex] : null;
        foreach (var item in items)
        {
            _playlist.Remove(item);
        }

        _currentIndex = current is null ? -1 : _playlist.IndexOf(current);
        if (current is not null && _currentIndex < 0)
        {
            _mediaPlayer.Stop();
        }

        RefreshQueuedFlags();
    }

    private void ClearPlaylist_Click(object sender, RoutedEventArgs e)
    {
        _mediaPlayer.Stop();
        _playlist.Clear();
        _currentIndex = -1;
        EmptyHint.Visibility = Visibility.Visible;
        Title = "נגן וידאו";
        RefreshQueuedFlags();
    }

    // ---------- Explorer ----------

    private void LoadDrives()
    {
        foreach (var drive in DriveInfo.GetDrives().Where(d => d.IsReady))
        {
            FolderTree.Items.Add(CreateFolderNode(drive.Name, drive.RootDirectory.FullName, ShellImages.GetDriveIcon(drive.Name)));
        }
    }

    private static TreeViewItem CreateFolderNode(string header, string path, ImageSource? icon)
    {
        var node = new TreeViewItem { Header = CreateItemHeader(header, icon), Tag = path };
        node.Items.Add(new TreeViewItem { Header = "..." });
        return node;
    }

    private static StackPanel CreateItemHeader(string text, ImageSource? icon)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        panel.Children.Add(new Image { Source = icon, Width = 16, Height = 16, Margin = new Thickness(0, 0, 6, 0) });
        panel.Children.Add(new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center });
        return panel;
    }

    private void FolderTree_Expanded(object sender, RoutedEventArgs e)
    {
        // Expanded bubbles from children; only handle the node that was actually expanded.
        if (e.OriginalSource != sender || sender is not TreeViewItem node || node.Tag is not string path)
        {
            return;
        }

        if (node.Items.Count != 1 || node.Items[0] is not TreeViewItem { Tag: null })
        {
            return;
        }

        node.Items.Clear();
        try
        {
            foreach (var dir in Directory.EnumerateDirectories(path).OrderBy(d => d, StringComparer.CurrentCultureIgnoreCase))
            {
                node.Items.Add(CreateFolderNode(Path.GetFileName(dir), dir, ShellImages.GetFolderIcon()));
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
        }
    }

    private void FolderTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (e.NewValue is not TreeViewItem { Tag: string path })
        {
            return;
        }

        try
        {
            ExplorerFiles.ItemsSource = GetMediaFiles(path).Select(f => new PlaylistItem(f)).ToList();
            RefreshQueuedFlags();
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            ExplorerFiles.ItemsSource = null;
        }
    }

    private void AddExplorerSelection_Click(object sender, RoutedEventArgs e) => AddExplorerSelection(play: false);

    private void ExplorerFiles_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (!IsFromButton(e.OriginalSource))
        {
            AddExplorerSelection(play: true);
        }
    }

    private void AddExplorerSelection(bool play)
    {
        var paths = ExplorerFiles.SelectedItems.OfType<PlaylistItem>().Select(i => i.FilePath).ToList();
        if (paths.Count == 0)
        {
            return;
        }

        var firstIndex = AddFiles(paths);
        if (play)
        {
            PlayIndex(firstIndex);
        }
    }

    // ---------- Subtitles ----------

    private static string? FindSidecarSubtitle(string videoPath)
    {
        var folder = Path.GetDirectoryName(videoPath);
        if (folder is null || !Directory.Exists(folder))
        {
            return null;
        }

        var baseName = Path.GetFileNameWithoutExtension(videoPath);
        try
        {
            // Matches "movie.srt" and language variants such as "movie.he.srt" or "movie.en.ass".
            return Directory.EnumerateFiles(folder)
                .Where(f => SubtitleExtensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase))
                .Where(f =>
                {
                    var name = Path.GetFileNameWithoutExtension(f);
                    return name.Equals(baseName, StringComparison.OrdinalIgnoreCase)
                        || name.StartsWith(baseName + ".", StringComparison.OrdinalIgnoreCase);
                })
                .OrderBy(f => Path.GetFileNameWithoutExtension(f).Length == baseName.Length ? 0 : 1)
                .ThenBy(f => IsHebrewSubtitleName(f) ? 0 : 1)
                .ThenBy(f => f, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static bool IsHebrewSubtitleName(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        return name.EndsWith(".he", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".heb", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".hebrew", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Per-file VLC options that control how subtitles are drawn and decoded.</summary>
    private void ApplySubtitleOptions(Media media)
    {
        media.AddOption($":freetype-font={_state.SubtitleFont}");
        media.AddOption($":freetype-fontsize={_state.SubtitleSize}");
        media.AddOption($":freetype-color={_state.SubtitleColor}");
        if (_state.SubtitleEncoding != "auto")
        {
            media.AddOption($":subsdec-encoding={_state.SubtitleEncoding}");
        }
    }

    /// <summary>Per-file filters. VLC reads these options only when the media opens.</summary>
    private static void ApplyMediaFilters(Media media, PostProcessingSettings settings)
    {
        if (settings.Sharpen)
        {
            media.AddOption(":video-filter=sharpen");
        }

        if (settings.NormalizeAudio)
        {
            media.AddOption(":audio-filter=compressor");
            media.AddOption(":compressor-threshold=-20");
            media.AddOption(":compressor-ratio=4");
            media.AddOption(":compressor-makeup-gain=6");
        }
    }

    /// <summary>Picture adjustments and deinterlacing. These apply to the running player at once.</summary>
    private void ApplyVideoAdjustments(PostProcessingSettings settings)
    {
        _mediaPlayer.SetAdjustInt(VideoAdjustOption.Enable, 1);
        _mediaPlayer.SetAdjustFloat(VideoAdjustOption.Brightness, settings.Brightness);
        _mediaPlayer.SetAdjustFloat(VideoAdjustOption.Contrast, settings.Contrast);
        _mediaPlayer.SetAdjustFloat(VideoAdjustOption.Saturation, settings.Saturation);
        _mediaPlayer.SetDeinterlace(settings.Deinterlace ? "yadif" : null!);
    }

    private void PostProcessing_Click(object sender, RoutedEventArgs e)
    {
        var draft = _state.PostProcessing.Clone();
        var window = new PostProcessingWindow(draft, ApplyVideoAdjustments) { Owner = this };
        if (window.ShowDialog() != true)
        {
            ApplyVideoAdjustments(_state.PostProcessing);
            return;
        }

        var needsReopen = draft.Sharpen != _state.PostProcessing.Sharpen
            || draft.NormalizeAudio != _state.PostProcessing.NormalizeAudio;
        _state.PostProcessing = draft;
        _state.Save();

        if (needsReopen && _media is not null && _currentIndex >= 0)
        {
            PlayIndex(_currentIndex, Math.Max(0, _mediaPlayer.Time), _currentSubtitle);
        }

        ShowOsd("עיבוד התמונה והקול נשמרו");
    }

    private void Pip_Click(object sender, RoutedEventArgs e)
    {
        Topmost = !Topmost;
        if (sender is MenuItem item)
        {
            item.IsChecked = Topmost;
        }

        ShowOsd(Topmost ? "תמונה בתוך תמונה: פעיל" : "תמונה בתוך תמונה: כבוי");
    }

    /// <summary>Shows the time under the mouse on the seekbar, with a preview frame when one is available.</summary>
    private void SeekSlider_MouseMove(object sender, MouseEventArgs e)
    {
        var length = _mediaPlayer.Length;
        var width = SeekSlider.ActualWidth;
        if (length <= 0 || width <= 0 || _currentIndex < 0)
        {
            ThumbPopup.IsOpen = false;
            return;
        }

        var x = Math.Clamp(e.GetPosition(SeekSlider).X, 0, width);
        var timeMs = (long)(x / width * length);
        _hoverBucket = ThumbnailGenerator.BucketOf(timeMs);
        ThumbTimeText.Text = FormatTime(timeMs);

        if (_thumbs.TryGet(_hoverBucket, out var image))
        {
            ThumbImage.Source = image;
        }
        else
        {
            ThumbImage.Source = null;
            _thumbRequestTimer.Stop();
            _thumbRequestTimer.Start();
        }

        // Relative placement: offsets are measured from the seekbar's top-left corner.
        var popupWidth = ThumbPopup.Child is FrameworkElement { ActualWidth: > 0 } content ? content.ActualWidth : 206;
        var popupHeight = ThumbPopup.Child is FrameworkElement { ActualHeight: > 0 } heightContent ? heightContent.ActualHeight : 150;
        ThumbPopup.HorizontalOffset = Math.Clamp(x - popupWidth / 2, 0, Math.Max(0, width - popupWidth));
        ThumbPopup.VerticalOffset = -(popupHeight + 8);
        ThumbPopup.IsOpen = true;
    }

    private void SeekSlider_MouseLeave(object sender, MouseEventArgs e)
    {
        _thumbRequestTimer.Stop();
        ThumbPopup.IsOpen = false;
    }

    /// <summary>Runs once the mouse has rested on the seekbar; decodes the preview frame for the hovered bucket.</summary>
    private void RequestHoverThumbnail()
    {
        _thumbRequestTimer.Stop();
        if (!ThumbPopup.IsOpen || _currentIndex < 0 || _thumbs.TryGet(_hoverBucket, out _))
        {
            return;
        }

        _thumbs.Request(_playlist[_currentIndex].FilePath, (long)_hoverBucket * ThumbnailGenerator.BucketSeconds * 1000);
    }

    private void OnThumbnailReady(int bucket, System.Windows.Media.ImageSource image)
    {
        if (bucket == _hoverBucket && ThumbPopup.IsOpen)
        {
            ThumbImage.Source = image;
        }
    }

    private void SubtitleSettings_Click(object sender, RoutedEventArgs e)
    {
        var window = new SubtitleSettingsWindow(_state) { Owner = this };
        if (window.ShowDialog() != true)
        {
            return;
        }

        _state.SubtitleFont = window.SelectedFont;
        _state.SubtitleSize = window.SelectedSize;
        _state.SubtitleColor = window.SelectedColor;
        _state.SubtitleEncoding = window.SelectedEncoding;
        _state.Save();

        if (_media is not null && _currentIndex >= 0)
        {
            // Subtitle options apply when media opens, so reopen at the current position.
            PlayIndex(_currentIndex, Math.Max(0, _mediaPlayer.Time), _currentSubtitle);
        }

        ShowOsd("הגדרות הכתוביות נשמרו");
    }

    private void ReloadWithSubtitle(string subtitlePath)
    {
        if (_currentIndex < 0)
        {
            return;
        }

        PlayIndex(_currentIndex, Math.Max(0, _mediaPlayer.Time), subtitlePath);
        ShowOsd("הכתוביות נטענו");
    }

    private void LoadSubtitle_Click(object sender, RoutedEventArgs e)
    {
        if (_currentIndex < 0)
        {
            ShowOsd("פתח קובץ וידאו לפני טעינת כתוביות");
            return;
        }

        var dialog = new OpenFileDialog
        {
            Title = "בחר קובץ כתוביות",
            Filter = "קבצי כתוביות|*.srt;*.ass;*.ssa;*.vtt|כל הקבצים|*.*",
        };
        if (dialog.ShowDialog(this) == true)
        {
            ReloadWithSubtitle(dialog.FileName);
        }
    }

    private void EditSubtitles_Click(object sender, RoutedEventArgs e)
    {
        if (_currentIndex < 0)
        {
            ShowOsd("אין קובץ וידאו פעיל");
            return;
        }

        var subtitlePath = _currentSubtitle ?? Path.ChangeExtension(_playlist[_currentIndex].FilePath, ".srt");
        if (!string.Equals(Path.GetExtension(subtitlePath), ".srt", StringComparison.OrdinalIgnoreCase))
        {
            MessageBox.Show(this, "עריכה נתמכת רק בקבצי SRT.", "עריכת כתוביות", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var editor = new SubtitleEditorWindow(subtitlePath) { Owner = this };
        editor.ShowDialog();
        if (editor.Saved)
        {
            ReloadWithSubtitle(subtitlePath);
        }
    }

    private void SubtitleTracksMenu_SubmenuOpened(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem menu)
        {
            return;
        }

        menu.Items.Clear();
        var tracks = _mediaPlayer.SpuDescription ?? [];
        if (tracks.Length == 0)
        {
            menu.Items.Add(new MenuItem { Header = "אין מסלולי כתוביות", IsEnabled = false });
            return;
        }

        foreach (var track in tracks)
        {
            var id = track.Id;
            var item = new MenuItem
            {
                Header = track.Name,
                IsCheckable = true,
                IsChecked = _mediaPlayer.Spu == id,
            };
            item.Click += (_, _) =>
            {
                _mediaPlayer.SetSpu(id);
                _lastSpuId = id;
                ShowOsd($"כתוביות: {track.Name}");
            };
            menu.Items.Add(item);
        }
    }

    private void AudioTracksMenu_SubmenuOpened(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem menu)
        {
            return;
        }

        menu.Items.Clear();
        var tracks = _mediaPlayer.AudioTrackDescription ?? [];
        if (tracks.Length == 0)
        {
            menu.Items.Add(new MenuItem { Header = "אין מסלולי שמע", IsEnabled = false });
            return;
        }

        foreach (var track in tracks)
        {
            var id = track.Id;
            var item = new MenuItem
            {
                Header = track.Name,
                IsCheckable = true,
                IsChecked = _mediaPlayer.AudioTrack == id,
            };
            item.Click += (_, _) =>
            {
                _mediaPlayer.SetAudioTrack(id);
                ShowOsd($"שמע: {track.Name}");
            };
            menu.Items.Add(item);
        }
    }

    private void VideoTracksMenu_SubmenuOpened(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem menu)
        {
            return;
        }

        menu.Items.Clear();
        var tracks = _mediaPlayer.VideoTrackDescription ?? [];
        if (tracks.Length == 0)
        {
            menu.Items.Add(new MenuItem { Header = "אין מסלולי וידאו", IsEnabled = false });
            return;
        }

        foreach (var track in tracks)
        {
            var id = track.Id;
            var item = new MenuItem
            {
                Header = track.Name,
                IsCheckable = true,
                IsChecked = _mediaPlayer.VideoTrack == id,
            };
            item.Click += (_, _) =>
            {
                _mediaPlayer.SetVideoTrack(id);
                ShowOsd($"וידאו: {track.Name}");
            };
            menu.Items.Add(item);
        }
    }

    private void ToggleSubtitles_Click(object sender, RoutedEventArgs e)
    {
        if (_mediaPlayer.Spu >= 0)
        {
            _lastSpuId = _mediaPlayer.Spu;
            _mediaPlayer.SetSpu(-1);
            ShowOsd("כתוביות כבויות");
            return;
        }

        var id = _lastSpuId >= 0 ? _lastSpuId : FirstSubtitleTrackId();
        if (id < 0)
        {
            ShowOsd("אין כתוביות זמינות");
            return;
        }

        _mediaPlayer.SetSpu(id);
        ShowOsd("כתוביות מופעלות");
    }

    private int FirstSubtitleTrackId()
    {
        foreach (var track in _mediaPlayer.SpuDescription ?? [])
        {
            if (track.Id >= 0)
            {
                return track.Id;
            }
        }

        return -1;
    }

    private void AdjustSubtitleDelay(long deltaMs)
    {
        _mediaPlayer.SetSpuDelay(_mediaPlayer.SpuDelay + deltaMs * 1000);
        ShowOsd($"עיכוב כתוביות: {_mediaPlayer.SpuDelay / 1000} ms");
    }

    private void SubDelayUp_Click(object sender, RoutedEventArgs e) => AdjustSubtitleDelay(100);

    private void SubDelayDown_Click(object sender, RoutedEventArgs e) => AdjustSubtitleDelay(-100);

    private void SubDelayReset_Click(object sender, RoutedEventArgs e)
    {
        _mediaPlayer.SetSpuDelay(0);
        ShowOsd("עיכוב כתוביות אופס");
    }

    // ---------- Menu / transport handlers ----------

    private void OpenFile_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "בחר קובץ", Multiselect = true, Filter = "כל הקבצים|*.*" };
        if (dialog.ShowDialog(this) == true)
        {
            PlayIndex(AddFiles(dialog.FileNames));
        }
    }

    private void AddFiles_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "הוסף קבצים לרשימה", Multiselect = true, Filter = "כל הקבצים|*.*" };
        if (dialog.ShowDialog(this) == true)
        {
            PlayIfIdle(AddFiles(dialog.FileNames));
        }
    }

    private void AddFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "בחר תיקייה" };
        if (dialog.ShowDialog(this) == true)
        {
            PlayIfIdle(AddFiles(GetMediaFiles(dialog.FolderName)));
        }
    }

    private void Exit_Click(object sender, RoutedEventArgs e) => Close();

    private void PlayPause_Click(object sender, RoutedEventArgs e)
    {
        if (_media is null || _mediaPlayer.State is VLCState.Stopped or VLCState.Ended or VLCState.NothingSpecial)
        {
            PlayIndex(_currentIndex >= 0 ? _currentIndex : 0);
            return;
        }

        _mediaPlayer.Pause();
    }

    private void Stop_Click(object sender, RoutedEventArgs e)
    {
        RememberPosition();
        _mediaPlayer.Stop();
    }

    private void Next_Click(object sender, RoutedEventArgs e) => PlayIndex(_currentIndex + 1);

    private void Prev_Click(object sender, RoutedEventArgs e)
    {
        if (_currentIndex <= 0 || _mediaPlayer.Time > RestartThresholdMs)
        {
            _mediaPlayer.Time = 0;
            return;
        }

        PlayIndex(_currentIndex - 1);
    }

    private void SpeedUp_Click(object sender, RoutedEventArgs e) =>
        SetSpeedIndex(Math.Min(Array.IndexOf(Speeds, _rate) + 1, Speeds.Length - 1));

    private void SpeedDown_Click(object sender, RoutedEventArgs e) =>
        SetSpeedIndex(Math.Max(Array.IndexOf(Speeds, _rate) - 1, 0));

    private void SpeedReset_Click(object sender, RoutedEventArgs e) => SetSpeedIndex(Array.IndexOf(Speeds, 1f));

    private void Mute_Click(object sender, RoutedEventArgs e)
    {
        _mediaPlayer.Mute = !_mediaPlayer.Mute;
        MuteButton.Content = _mediaPlayer.Mute ? "השתק" : "בטל השתקה";
        ShowOsd(_mediaPlayer.Mute ? "ההשתקה בוטלה" : "מושתק", atTopRight: true);
    }

    private void Fullscreen_Click(object sender, RoutedEventArgs e) => SetFullscreen(!_isFullscreen);

    private IntPtr MouseHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && wParam == (IntPtr)WmLButtonDown && GetForegroundWindow() == _windowHandle)
        {
            var info = Marshal.PtrToStructure<MsLLHookStruct>(lParam);
            if (IsPointOverVideo(info.Pt) && IsDoubleClick(info))
            {
                Dispatcher.BeginInvoke(new Action(() => SetFullscreen(!_isFullscreen)));
            }
        }
        else if (nCode >= 0 && wParam == (IntPtr)WmMouseWheel && GetForegroundWindow() == _windowHandle)
        {
            var info = Marshal.PtrToStructure<MsLLHookStruct>(lParam);
            if (IsPointOverVideo(info.Pt))
            {
                var delta = (short)((info.MouseData >> 16) & 0xFFFF);
                var step = delta > 0 ? VolumeStep : -VolumeStep;
                Dispatcher.BeginInvoke(new Action(() => ChangeVolume(step)));
            }
        }
        else if (nCode >= 0 && wParam == (IntPtr)WmRButtonUp && GetForegroundWindow() == _windowHandle)
        {
            var info = Marshal.PtrToStructure<MsLLHookStruct>(lParam);
            if (IsPointOverVideo(info.Pt))
            {
                Dispatcher.BeginInvoke(new Action(ShowVideoContextMenu));
            }
        }

        return CallNextHookEx(_mouseHook, nCode, wParam, lParam);
    }

    private bool IsPointOverVideo(NativePoint point)
    {
        var topLeft = VideoView.PointToScreen(new Point(0, 0));
        var bottomRight = VideoView.PointToScreen(new Point(VideoView.ActualWidth, VideoView.ActualHeight));
        return point.X >= topLeft.X && point.X < bottomRight.X && point.Y >= topLeft.Y && point.Y < bottomRight.Y;
    }

    private bool IsDoubleClick(MsLLHookStruct info)
    {
        var withinTime = _hasLastVideoClick && info.Time - _lastVideoClickTime <= GetDoubleClickTime();
        var withinDistance = withinTime
            && Math.Abs(info.Pt.X - _lastVideoClickPoint.X) <= GetSystemMetrics(SmCxDoubleClick) / 2
            && Math.Abs(info.Pt.Y - _lastVideoClickPoint.Y) <= GetSystemMetrics(SmCyDoubleClick) / 2;

        if (withinDistance)
        {
            _hasLastVideoClick = false;
            return true;
        }

        _hasLastVideoClick = true;
        _lastVideoClickTime = info.Time;
        _lastVideoClickPoint = info.Pt;
        return false;
    }

    private void ThinProgress_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_mediaPlayer.Length <= 0 || ThinProgress.ActualWidth <= 0)
        {
            return;
        }

        var ratio = Math.Clamp(e.GetPosition(ThinProgress).X / ThinProgress.ActualWidth, 0, 1);
        _mediaPlayer.Position = (float)ratio;
        ThinProgress.Value = ratio * ThinProgress.Maximum;
    }

#pragma warning disable WPF0001 // ThemeMode is evaluation-only in .NET 10 WPF; it is the supported way to switch Fluent themes.
    private void Theme_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { Tag: string tag })
        {
            Application.Current.ThemeMode = tag switch
            {
                "Light" => ThemeMode.Light,
                "Dark" => ThemeMode.Dark,
                _ => ThemeMode.System,
            };
        }
    }
#pragma warning restore WPF0001

    private void SetFullscreen(bool on)
    {
        if (on == _isFullscreen)
        {
            return;
        }

        _isFullscreen = on;
        var columns = new[] { SidePanelColumn, SideSplitterColumn };
        var panels = new UIElement[] { MenuBar, SidePanel, SideSplitter };
        var visibility = on ? Visibility.Collapsed : Visibility.Visible;
        foreach (var panel in panels)
        {
            panel.Visibility = visibility;
        }

        ControlsPanel.Visibility = on ? Visibility.Collapsed : Visibility.Visible;
        ThinProgress.Visibility = on ? Visibility.Visible : Visibility.Collapsed;

        if (on)
        {
            _savedColumnWidths = columns.Select(c => c.Width).ToArray();
            _windowStateBeforeFullscreen = WindowState;
            _normalBounds = WindowState == WindowState.Maximized
                ? RestoreBounds
                : new Rect(Left, Top, Width, Height);
            foreach (var column in columns)
            {
                column.Width = new GridLength(0);
            }

            WindowStyle = WindowStyle.None;
            _savedResizeMode = ResizeMode;
            ResizeMode = ResizeMode.NoResize;
            WindowState = WindowState.Normal;
            CoverCurrentMonitor();
        }
        else
        {
            for (var i = 0; i < columns.Length; i++)
            {
                columns[i].Width = _savedColumnWidths?[i] ?? columns[i].Width;
            }

            ResizeMode = _savedResizeMode;
            WindowStyle = WindowStyle.SingleBorderWindow;
            WindowState = WindowState.Normal;
            Left = _normalBounds.Left;
            Top = _normalBounds.Top;
            Width = _normalBounds.Width;
            Height = _normalBounds.Height;
            if (_windowStateBeforeFullscreen == WindowState.Maximized)
            {
                WindowState = WindowState.Maximized;
            }
        }
    }

    /// <summary>Sizes the window to the full bounds of its monitor, including the taskbar area.</summary>
    private void CoverCurrentMonitor()
    {
        var info = new MonitorInfo { cbSize = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(MonitorFromWindow(_windowHandle, MonitorDefaultToNearest), ref info))
        {
            return;
        }

        var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(this);
        Left = info.Left / dpi.DpiScaleX;
        Top = info.Top / dpi.DpiScaleY;
        Width = (info.Right - info.Left) / dpi.DpiScaleX;
        Height = (info.Bottom - info.Top) / dpi.DpiScaleY;
    }

    private void PlayPlaylistSelection()
    {
        if (PlaylistList.SelectedIndex >= 0)
        {
            PlayIndex(PlaylistList.SelectedIndex);
        }
    }

    private void Shortcuts_Click(object sender, RoutedEventArgs e)
    {
        const string text =
            "רווח / K — ניגון והשהיה\n" +
            "← / → — קפיצה 5 שניות (Ctrl: 30 שניות)\n" +
            "↑ / ↓ — ווליום ±5%\n" +
            "M — השתקה\n" +
            "[ / ] — הורדה / העלאה של מהירות ניגון\n" +
            "Backspace — מהירות רגילה (1x)\n" +
            "N / P — פריט הבא / קודם (P מתחיל מחדש אם עברו 3 שניות)\n" +
            "F / F11 — מסך מלא (Esc ליציאה)\n" +
            "S — הצג / הסתר כתוביות\n" +
            "G / H — הקדמה / עיכוב כתוביות ב-100ms\n" +
            ", / . — פריים אחורה / קדימה (מעבר פריים מדויק)\n" +
            "L — לולאה על הפריט הנוכחי\n" +
            "T — תמונה בתוך תמונה (תמיד למעלה)\n" +
            "Ctrl+O — פתח קובץ\n" +
            "Ctrl+E — עריכת כתוביות\n" +
            "Delete — הסרת פריט נבחר מהרשימה\n" +
            "Enter — ניגון הפריט הנבחר בסייר או ברשימה";
        MessageBox.Show(this, text, "קיצורי מקלדת", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    // ---------- Window events ----------

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        var focused = Keyboard.FocusedElement;
        if (focused is TextBoxBase)
        {
            return;
        }

        var isArrow = e.Key is Key.Left or Key.Right or Key.Up or Key.Down;
        if (isArrow && focused is ListBox or TreeView or Slider or ComboBox)
        {
            return;
        }

        var handled = true;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            switch (e.Key)
            {
                case Key.O: OpenFile_Click(this, e); break;
                case Key.E: EditSubtitles_Click(this, e); break;
                case Key.Left: SeekBy(-30000); break;
                case Key.Right: SeekBy(30000); break;
                default: handled = false; break;
            }
        }
        else
        {
            switch (e.Key)
            {
                case Key.Space:
                case Key.K: PlayPause_Click(this, e); break;
                case Key.Left: SeekBy(-5000); break;
                case Key.Right: SeekBy(5000); break;
                case Key.Up: ChangeVolume(VolumeStep); break;
                case Key.Down: ChangeVolume(-VolumeStep); break;
                case Key.M: Mute_Click(this, e); break;
                case Key.OemOpenBrackets: SpeedDown_Click(this, e); break;
                case Key.OemCloseBrackets: SpeedUp_Click(this, e); break;
                case Key.Back: SpeedReset_Click(this, e); break;
                case Key.N: Next_Click(this, e); break;
                case Key.P: Prev_Click(this, e); break;
                case Key.F:
                case Key.F11: Fullscreen_Click(this, e); break;
                case Key.Escape:
                    if (_isFullscreen) SetFullscreen(false);
                    else handled = false;
                    break;
                case Key.S: ToggleSubtitles_Click(this, e); break;
                case Key.G: SubDelayDown_Click(this, e); break;
                case Key.H: SubDelayUp_Click(this, e); break;
                case Key.L: Loop_Click(this, e); break;
                case Key.T: Pip_Click(this, e); break;
                case Key.OemComma: FrameBackward_Click(this, e); break;
                case Key.OemPeriod: FrameForward_Click(this, e); break;
                case Key.Delete:
                    if (focused == PlaylistList) RemoveSelected_Click(this, e);
                    else handled = false;
                    break;
                case Key.Enter:
                    if (focused == ExplorerFiles) AddExplorerSelection(play: true);
                    else if (focused == PlaylistList) PlayPlaylistSelection();
                    else handled = false;
                    break;
                default: handled = false; break;
            }
        }

        e.Handled = handled;
    }

    private void Window_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void Window_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] paths)
        {
            return;
        }

        var files = paths.SelectMany(p => Directory.Exists(p) ? GetMediaFiles(p) : new[] { p }).ToList();
        PlayIfIdle(AddFiles(files));
    }

    private void Window_Closed(object? sender, EventArgs e)
    {
        RememberPosition();
        _positionTimer.Stop();
        _osdTimer.Stop();
        if (_mouseHook != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_mouseHook);
            _mouseHook = IntPtr.Zero;
        }
        VideoView.MediaPlayer = null;
        _mediaPlayer.Stop();
        _media?.Dispose();
        _mediaPlayer.Dispose();
        _thumbs.Dispose();
        _libVLC.Dispose();
    }

    // ---------- Recent files and defaults ----------

    private void RefreshRecentList()
    {
        _recent.Clear();
        foreach (var record in _state.Recent(RecentLimit))
        {
            var resume = record.PositionMs > 0 ? $"ממשיך מ-{FormatTime(record.PositionMs)}" : "נפתח לאחרונה";
            _recent.Add(new RecentEntry(record.Path, resume));
        }
    }

    private void OpenFileNow(string path)
    {
        var index = _playlist.ToList().FindIndex(p => string.Equals(p.FilePath, path, StringComparison.OrdinalIgnoreCase));
        if (index < 0)
        {
            index = AddFiles([path]);
        }

        PlayIndex(index);
    }

    private void RecentList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (RecentList.SelectedItem is RecentEntry entry)
        {
            OpenFileNow(entry.FilePath);
        }
    }

    private void ClearRecent_Click(object sender, RoutedEventArgs e)
    {
        _state.ClearRecent();
        _state.Save();
        RefreshRecentList();
    }

    private void OpenStartupFiles()
    {
        var files = Environment.GetCommandLineArgs().Skip(1).Where(File.Exists).ToList();
        if (files.Count > 0)
        {
            PlayIndex(AddFiles(files));
        }
    }

    private void CheckDefaultApp()
    {
        if (!_state.AskDefaultOnStartup)
        {
            return;
        }

        FileAssociations.Register();
        if (!FileAssociations.IsDefault())
        {
            FileAssociations.OpenDefaultAppsDialog();
        }
    }

    private void SetAsDefaultApp()
    {
        try
        {
            FileAssociations.Register();
            FileAssociations.OpenDefaultAppsDialog();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"לא ניתן להגדיר את הנגן כברירת מחדל:\n{ex.Message}",
                "נגן וידאו", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void SetDefaultApp_Click(object sender, RoutedEventArgs e) => SetAsDefaultApp();

    private void AskDefault_Click(object sender, RoutedEventArgs e) => SetAskDefault(AskDefaultMenuItem.IsChecked);

    private void SetAskDefault(bool enabled)
    {
        _state.AskDefaultOnStartup = enabled;
        AskDefaultMenuItem.IsChecked = enabled;
        _state.Save();
    }

    // ---------- Helpers ----------

    private void ShowOsd(string text, bool atTopRight = false)
    {
        OsdText.Text = text;
        _osdAtTopRight = atTopRight;
        // Reopening makes the popup run its placement callback again, so the position follows atTopRight.
        OsdPopup.IsOpen = false;
        OsdPopup.IsOpen = true;
        _osdTimer.Stop();
        _osdTimer.Start();
    }

    private CustomPopupPlacement[] PlaceOsd(Size popupSize, Size targetSize, Point offset)
    {
        var x = _osdAtTopRight ? targetSize.Width - popupSize.Width - 16 : (targetSize.Width - popupSize.Width) / 2;
        var y = _osdAtTopRight ? 16 : (targetSize.Height - popupSize.Height) / 2;
        return [new CustomPopupPlacement(new Point(x, y), PopupPrimaryAxis.Horizontal)];
    }

    private void ShowVideoContextMenu()
    {
        var menu = (ContextMenu)Resources["VideoContextMenu"];
        menu.PlacementTarget = VideoView;
        menu.Placement = PlacementMode.MousePoint;
        menu.IsOpen = true;
    }

    private static string FormatTime(long ms)
    {
        var time = TimeSpan.FromMilliseconds(Math.Max(0, ms));
        return time.TotalHours >= 1 ? time.ToString(@"h\:mm\:ss") : time.ToString(@"m\:ss");
    }
}

using System.Runtime.InteropServices;
using LibVLCSharp.Shared;

namespace VideoPlayer.Services;

/// <summary>
/// Produces small preview frames for the seekbar. Uses its own silent player that renders into
/// a fixed-size buffer (vmem), so the main player is not affected. Frames are cached per time bucket.
/// </summary>
public sealed class ThumbnailGenerator : IDisposable
{
    public const int Width = 192;
    public const int Height = 108;
    public const int BucketSeconds = 5;
    private const int MaxCachedFrames = 300;

    private readonly LibVLC _libVLC;
    private readonly System.Windows.Threading.Dispatcher _dispatcher;
    private readonly MediaPlayer _player;
    private readonly IntPtr _buffer;
    private readonly Dictionary<int, System.Windows.Media.ImageSource> _cache = new();
    private readonly Queue<int> _cacheOrder = new();
    private readonly MediaPlayer.LibVLCVideoLockCb _lockCb;
    private readonly MediaPlayer.LibVLCVideoUnlockCb _unlockCb;
    private readonly MediaPlayer.LibVLCVideoDisplayCb _displayCb;

    private string? _path;
    private bool _playing;
    private long _pendingTimeMs = -1;
    private int _pendingBucket = -1;
    private bool _disposed;

    /// <summary>Raised on the UI thread when a frame for <c>bucket</c> is ready.</summary>
    public event Action<int, System.Windows.Media.ImageSource>? ThumbnailReady;

    public ThumbnailGenerator(LibVLC libVLC, System.Windows.Threading.Dispatcher dispatcher)
    {
        _libVLC = libVLC;
        _dispatcher = dispatcher;
        _buffer = Marshal.AllocHGlobal(Width * Height * 4);

        _lockCb = (_, planes) =>
        {
            Marshal.WriteIntPtr(planes, _buffer);
            return IntPtr.Zero;
        };
        _unlockCb = (_, _, _) => { };
        _displayCb = (_, _) => OnFrame();

        _player = new MediaPlayer(_libVLC);
        _player.SetVideoFormat("RV32", Width, Height, Width * 4);
        _player.SetVideoCallbacks(_lockCb, _unlockCb, _displayCb);
        _player.Playing += (_, _) => OnPlaying();
    }

    public bool TryGet(int bucket, out System.Windows.Media.ImageSource image) => _cache.TryGetValue(bucket, out image!);

    /// <summary>Asks for the frame at <paramref name="timeMs"/> of <paramref name="path"/>. Results arrive via <see cref="ThumbnailReady"/>.</summary>
    public void Request(string path, long timeMs)
    {
        if (_disposed)
        {
            return;
        }

        if (!string.Equals(path, _path, StringComparison.OrdinalIgnoreCase))
        {
            _path = path;
            _playing = false;
            _cache.Clear();
            _cacheOrder.Clear();
            _pendingTimeMs = timeMs;
            _pendingBucket = BucketOf(timeMs);
            using var media = new Media(_libVLC, path, FromType.FromPath);
            media.AddOption(":no-audio");
            media.AddOption(":no-sub");
            _player.Play(media);
            return;
        }

        _pendingBucket = BucketOf(timeMs);
        if (_playing)
        {
            _player.Time = timeMs;
        }
        else
        {
            _pendingTimeMs = timeMs;
        }
    }

    public static int BucketOf(long timeMs) => (int)(timeMs / (BucketSeconds * 1000L));

    private void OnPlaying()
    {
        _playing = true;
        var seekTo = _pendingTimeMs;
        Task.Run(() =>
        {
            _player.SetPause(true);
            if (seekTo >= 0)
            {
                _player.Time = seekTo;
            }
        });
    }

    /// <summary>Runs on a LibVLC thread. Copies the frame out before the buffer is reused.</summary>
    private void OnFrame()
    {
        var bucket = _pendingBucket;
        if (bucket < 0)
        {
            return;
        }

        var bytes = new byte[Width * Height * 4];
        Marshal.Copy(_buffer, bytes, 0, bytes.Length);
        _dispatcher.BeginInvoke(new Action(() => Store(bucket, bytes)));
    }

    private void Store(int bucket, byte[] bytes)
    {
        if (_disposed)
        {
            return;
        }

        var bitmap = System.Windows.Media.Imaging.BitmapSource.Create(
            Width, Height, 96, 96, System.Windows.Media.PixelFormats.Bgra32, null, bytes, Width * 4);
        bitmap.Freeze();

        if (!_cache.ContainsKey(bucket))
        {
            _cacheOrder.Enqueue(bucket);
        }
        _cache[bucket] = bitmap;

        while (_cacheOrder.Count > MaxCachedFrames)
        {
            _cache.Remove(_cacheOrder.Dequeue());
        }

        ThumbnailReady?.Invoke(bucket, bitmap);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _player.Stop();
        _player.Dispose();
        Marshal.FreeHGlobal(_buffer);
    }
}

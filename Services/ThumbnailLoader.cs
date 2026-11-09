using System.Collections.Concurrent;
using System.Windows;
using System.Windows.Media;
using VideoPlayer.Models;

namespace VideoPlayer.Services;

/// <summary>Loads playlist previews on one background STA thread, so big playlists don't block the UI.</summary>
internal static class ThumbnailLoader
{
    private const int ThumbnailSize = 128;

    private static readonly BlockingCollection<PlaylistItem> Queue = new();

    static ThumbnailLoader()
    {
        var thread = new Thread(Run) { IsBackground = true, Name = "ThumbnailLoader" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
    }

    public static void Enqueue(PlaylistItem item) => Queue.Add(item);

    private static void Run()
    {
        foreach (var item in Queue.GetConsumingEnumerable())
        {
            ImageSource? image;
            try
            {
                image = item.IsAudio
                    ? ShellImages.GetAudioCover(item.FilePath, ThumbnailSize)
                    : ShellImages.GetShellThumbnail(item.FilePath, ThumbnailSize);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                image = null;
            }

            if (image is null)
            {
                continue;
            }

            Application.Current?.Dispatcher.BeginInvoke(new Action(() => item.Thumbnail = image));
        }
    }
}

using System.IO;
using System.Windows.Media;
using VideoPlayer.Services;

namespace VideoPlayer.Models;

public sealed class RecentEntry(string filePath, string resumeText)
{
    public string FilePath { get; } = filePath;

    public string DisplayName { get; } = Path.GetFileName(filePath);

    /// <summary>Text under the name: where playback resumes, or that the file was simply opened.</summary>
    public string ResumeText { get; } = resumeText;

    public ImageSource? Icon { get; } = ShellImages.GetFileIcon(filePath);
}

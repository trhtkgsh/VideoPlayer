namespace VideoPlayer.Services;

/// <summary>Picture and sound adjustments applied by the player.</summary>
public sealed class PostProcessingSettings
{
    /// <summary>1 is neutral. Adjusted live.</summary>
    public float Brightness { get; set; } = 1f;

    /// <summary>1 is neutral. Adjusted live.</summary>
    public float Contrast { get; set; } = 1f;

    /// <summary>1 is neutral. Adjusted live.</summary>
    public float Saturation { get; set; } = 1f;

    /// <summary>Removes interlacing lines from old video. Adjusted live.</summary>
    public bool Deinterlace { get; set; }

    /// <summary>Applied when a file opens.</summary>
    public bool Sharpen { get; set; }

    /// <summary>Applied when a file opens. Lowers loud parts and raises quiet speech.</summary>
    public bool NormalizeAudio { get; set; }

    public void CopyFrom(PostProcessingSettings other)
    {
        Brightness = other.Brightness;
        Contrast = other.Contrast;
        Saturation = other.Saturation;
        Deinterlace = other.Deinterlace;
        Sharpen = other.Sharpen;
        NormalizeAudio = other.NormalizeAudio;
    }

    public PostProcessingSettings Clone()
    {
        var copy = new PostProcessingSettings();
        copy.CopyFrom(this);
        return copy;
    }
}

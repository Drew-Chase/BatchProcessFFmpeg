// LFInteractive LLC. - All Rights Reserved

namespace BatchProcessFFmpeg.Models;

internal struct SettingsFile
{
    public SettingsFile()
    {
    }

    public string AudioBitrate { get; set; } = "";

    public string AudioCodec { get; set; } = "";

    public int ConcurrentProcesses { get; set; } = 3;

    public bool Overwrite { get; set; } = false;

    public string PixelFormat { get; set; } = "yuv420p";

    public string VideoBitrate { get; set; } = "";

    public string VideoCodec { get; set; } = "h264";
}
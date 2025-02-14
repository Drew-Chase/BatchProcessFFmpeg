// LFInteractive LLC. - All Rights Reserved

namespace BatchProcessFFmpeg.Models;

internal struct ManifestFile
{
    public float AverageSpeed { get; set; }
    public long AverageTime { get; set; }
    public bool NeedsToRefresh { get; set; }
    public long RescanTime { get; set; }
    public long TotalBytesRemaining { get; set; }
    public long TotalBytesSaved { get; set; }
    public long TotalEstamatedBytesToSave { get; set; }
}
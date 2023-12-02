// LFInteractive LLC. - All Rights Reserved
namespace BatchProcessFFmpeg.Models;

internal readonly struct ProcessedFile
{
    public double average_speed { get; init; }
    public string file { get; init; }
    public long new_size { get; init; }
    public long original_size { get; init; }
    public bool successful { get; init; }
    public long time { get; init; }
    public TimeSpan video_duration { get; init; }
}
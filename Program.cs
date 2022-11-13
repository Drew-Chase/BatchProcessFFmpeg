using Chase.FFmpeg.Converters;
using Chase.FFmpeg.Downloader;
using Chase.FFmpeg.Extra;
using Chase.FFmpeg.Info;
using ChaseLabs.CLConfiguration;
using CLMath;
using System.Text;
using Timer = System.Timers.Timer;

namespace BatchProcessFFmpeg;


internal class Program
{
    static void Main(string[] args)
    {
        long start = DateTime.Now.Ticks;
        string exe_dir = Environment.CurrentDirectory;
        if (args.Any())
        {
            Environment.CurrentDirectory = Path.GetFullPath(args[0]);
        }
        ConfigManager user_settings = new(Environment.CurrentDirectory.Replace(Path.DirectorySeparatorChar, '_').Replace(":", ""), exe_dir);
        int concurrent = user_settings.GetOrCreate("concurrent_processes", 3).Value;
        long saved_bytes = user_settings.GetOrCreate("saved_bytes", 0).Value;
        string video_codec = user_settings.GetOrCreate("video_codec", "h264").Value;
        string audio_codec = user_settings.GetOrCreate("audio_codec", "aac").Value;
        string video_bitrate = user_settings.GetOrCreate("video_bitrate", "").Value;
        string audio_bitrate = user_settings.GetOrCreate("audio_bitrate", "").Value;
        string pixel_format = user_settings.GetOrCreate("pixel_format", "yuv420p").Value;
        bool overwrite = user_settings.GetOrCreate("overwrite", true).Value;
        Dictionary<string, long> processed = user_settings.GetOrCreate("processed", new Dictionary<string, long>()).Value;

        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine($"[ {DateTime.Now:MM/dd/yyyy - h:mmtt} ] Checking FFmpeg...");
        FFmpegDownloader.Instance.GetLatest(Path.Combine(exe_dir, "ffmpeg")).Wait();
        Console.WriteLine($"[ {DateTime.Now:MM/dd/yyyy - h:mmtt} ] Scanning Files...");
        Console.ResetColor();
        var files = FFVideoUtility.GetFiles(Environment.CurrentDirectory, true).OrderBy(i => new FileInfo(i).Length).Reverse();


        Dictionary<string, string> output = new();
        Dictionary<string, int> id = new();

        Timer timer = new(1000)
        {
            AutoReset = true,
            Enabled = true,
        };
        timer.Elapsed += (s, e) =>
        {
            Console.Clear();
            Console.CursorTop = 0;
            Console.CursorLeft = 0;
            Console.ForegroundColor = ConsoleColor.Magenta;
            Console.WriteLine($"================================= PROCESSING {Environment.CurrentDirectory} ============================");
            StringBuilder builder = new();

            foreach (var o in output)
            {
                builder.AppendLine(o.Value);
            }

            Console.CursorTop = 1;
            Console.CursorLeft = 0;
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine(builder);
            Console.CursorTop = concurrent + 2;
            Console.CursorLeft = 0;
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"Saved {CLFileMath.AdjustedFileSize(saved_bytes)}!");
            Console.ForegroundColor = ConsoleColor.Cyan;
            TimeSpan runtime = new(DateTime.Now.Ticks - start);
            StringBuilder time_builder = new();
            if (runtime.Days > 0)
            {
                time_builder.Append($"{runtime.Days} days ");
            }
            if (runtime.Hours > 0)
            {
                time_builder.Append($"{runtime.Hours} hours ");
            }
            if (runtime.Minutes > 0)
            {
                time_builder.Append($"{runtime.Minutes} minutes ");
            }
            if (runtime.Seconds > 0)
            {
                time_builder.Append($"{runtime.Seconds} seconds");
            }
            Console.WriteLine($"Runtime: {time_builder}");
            Console.ResetColor();
        };
        timer.Start();

        int index = 0;
        string tmp_dir = Directory.CreateDirectory(Path.Combine(exe_dir, "tmp")).FullName;

        Parallel.ForEach(files, new() { MaxDegreeOfParallelism = concurrent }, file =>
        {
            FileInfo fileInfo = new(file);
            FFMediaInfo info = new(file);
            FFMuxedConverter converter = FFMuxedConverter.SetMedia(info);
            converter.ChangeHardwareAccelerationMethod();
            if (!string.IsNullOrWhiteSpace(video_codec))
                converter.ChangeVideoCodec(video_codec);

            if (!string.IsNullOrWhiteSpace(audio_codec))
                converter.ChangeAudioCodec(audio_codec);

            if (!string.IsNullOrWhiteSpace(video_bitrate))
                converter.ChangeVideoBitrate(video_bitrate);

            if (!string.IsNullOrWhiteSpace(audio_bitrate))
                converter.ChangeAudioBitrate(audio_bitrate);

            if (!string.IsNullOrWhiteSpace(pixel_format))
                converter.ChangePixelFormat(pixel_format);

            converter.OverwriteOriginal();


            string tmp = Path.Combine(tmp_dir, $"{info.Filename}_tmp{fileInfo.Extension}");
            var process = converter.Convert(tmp, null, (s, e) =>
            {
                try
                {
                    if (!id.ContainsKey(file))
                    {
                        index++;
                        id.Add(file, index);
                    }

                    StringBuilder o = new();
                    o.Append($"[ {DateTime.Now:MM/dd/yyyy - h:mmtt} ({id[file]}/{(files.Count() + 1)})] {(e.Percentage / 100):p2} | ");
                    for (int i = 0; i < 20; i++)
                    {
                        if (e.Percentage >= i * 5)
                        {
                            o.Append('=');
                        }
                        else
                        {
                            o.Append(' ');
                        }
                    }
                    o.Append($" | {e.Speed:n2}x Speed | {info.SizeENG}");
                    if (!output.ContainsKey(file))
                    {
                        output.Add(file, o.ToString());
                    }
                    else
                    {
                        output[file] = o.ToString();
                    }
                }
                catch { }
            });

            if (output.ContainsKey(file))
                output.Remove(file);
            if (id.ContainsKey(file))
                id.Remove(file);
            if (process.ExitCode == 0)
            {
                long new_size = new FileInfo(tmp).Length;
                if (new_size < fileInfo.Length)
                {
                    long file_saved = fileInfo.Length - new_size;
                    saved_bytes += file_saved;
                    user_settings.GetOrCreate("saved_bytes", 0).Value = saved_bytes;
                    if (overwrite)
                    {
                        File.Move(tmp, file, true);
                        processed.Add(file, file_saved);
                        user_settings.GetOrCreate("processed", new Dictionary<string, long>()).Value = processed;
                    }
                }
            }else
            {
                File.Delete(tmp);
            }

        });
    }
}
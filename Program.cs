// LFInteractive LLC. - All Rights Reserved
using Chase.FFmpeg.Converters;
using Chase.FFmpeg.Downloader;
using Chase.FFmpeg.Extra;
using Chase.FFmpeg.Info;
using CLMath;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using Timer = System.Timers.Timer;

namespace BatchProcessFFmpeg;

record ProcessedFile(string file, long time, long original_size, long new_size, TimeSpan video_duration, double average_speed, bool successful);

internal class Program
{
    private readonly List<Process> active_processes = new();
    private readonly List<string> error_files = new();
    private readonly List<string> files = new();
    private readonly Dictionary<string, int> id = new();
    private readonly List<string> moving_files = new();
    private readonly int offset = 0;
    private readonly Dictionary<string, string> output = new();
    private readonly string settings_file;
    private readonly List<double> speeds = new();
    private readonly List<double> reductions = new();
    private readonly List<double> durations = new();
    private readonly long start;
    private readonly string tmp_dir;
    private readonly int total_files = 0;
    private TimeSpan runtime;

    private readonly Timer update_screen_timer = new(500)
    {
        AutoReset = false,
        Enabled = true,
    };


    private readonly string workspace_dir;
    private string audio_bitrate = "";
    private string audio_codec = "aac";
    private int concurrent = 3;
    private int current_offset = 0;
    private int current_index = 0;
    private string current_status = "";
    private long est_time = 0;
    private bool overwrite = true;
    private bool paused = false;
    private string pixel_format = "yuv420p";
    private List<ProcessedFile> processed = new();
    private long saved_bytes = 0;
    private string video_bitrate = "";
    private string video_codec = "h264";
    bool stopping = false;
    private readonly long total_size = 0;

    private Program(string[] args)
    {
        string exe_dir = Environment.CurrentDirectory;
        if (args.Any())
        {
            Environment.CurrentDirectory = Path.GetFullPath(args[0]);
        }

        exe_dir = Directory.CreateDirectory(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LFInteractive", "Batch Process FFmpeg")).FullName;
        start = DateTime.Now.Ticks;

        UpdateScreen();
        update_screen_timer.Elapsed += (s, e) => UpdateScreen();

        workspace_dir = Directory.CreateDirectory(Path.Combine(exe_dir, Environment.CurrentDirectory.Replace(Path.DirectorySeparatorChar, '_').Replace(":", ""))).FullName;
        settings_file = Path.Combine(workspace_dir, $"settings.json");
        current_offset = processed?.Count ?? 0;
        tmp_dir = Directory.CreateDirectory(Path.Combine(workspace_dir, "tmp")).FullName;
        if (Directory.Exists(tmp_dir))
            Directory.Delete(tmp_dir, true);
        Directory.CreateDirectory(tmp_dir);

        if (!File.Exists(settings_file))
        {
            Save();
            OpenSaveFile();
            Console.Write("\nPress Any Key to Continue...");
            Console.ReadKey();
        }

        Load();

        Console.ForegroundColor = ConsoleColor.Yellow;
        current_status = "Checking FFmpeg...";
        FFmpegDownloader.Instance.GetLatest(Path.Combine(exe_dir, "ffmpeg")).Wait();
        current_status = "Scanning Files...";
        Console.ResetColor();
        try
        {
            files = FFVideoUtility.GetFiles(Environment.CurrentDirectory, true).OrderBy(i=>new FileInfo(i).Length).Reverse().ToList();
        }
        catch (Exception e)
        {
            Console.WriteLine(JsonConvert.SerializeObject(e, Formatting.Indented));
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"Error while scanning for files... Trying again!");
            Console.ResetColor();
            _ = new Program(new string[] { Environment.CurrentDirectory });
            return;
        }

        if (!files.Any())
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine("No files found!");
            Exit();
        }
        else
        {
            total_files = files.Count;

            if (processed.Any())
            {
                current_status = "Parsing Stats...";

                foreach (var process in processed)
                {
                    files.Remove(process.file);
                    speeds.Add(process.average_speed);
                    reductions.Add(process.new_size / process.original_size);
                    durations.Add(process.video_duration.TotalSeconds);
                }

            }
            offset = total_files - files.Count;
            AppDomain.CurrentDomain.ProcessExit += (s, e) => Exit();
            Console.CancelKeyPress += (s, e) => Exit();

            if (processed != null && processed.Any() && files.Any())
            {
                est_time = (long)processed.Average(i => i.time) * files.Count;
            }


            current_status = "Getting total size...";

            foreach (string file in files)
            {
                total_size += new FileInfo(file).Length;
            }

            current_status = "";
            ProcessQueue();
        }
    }

    private static void Main(string[] args)
    {
        _ = new Program(args);
    }

    private void Exit()
    {
        stopping = true;
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine("Exiting!!!!");
        Console.ResetColor();
        KillCurrent();
        if (Directory.Exists(tmp_dir))
            Directory.Delete(tmp_dir, true);
        Console.WriteLine("DONE!");
        Environment.Exit(0);
    }

    private string GetTime(TimeSpan span)
    {
        StringBuilder time_builder = new();
        if (span.Days > 0)
        {
            time_builder.Append($"{span.Days} days ");
        }
        if (span.Hours > 0)
        {
            time_builder.Append($"{span.Hours} hours ");
        }
        if (span.Minutes > 0)
        {
            time_builder.Append($"{span.Minutes} minutes ");
        }
        if (span.Seconds > 0)
        {
            time_builder.Append($"{span.Seconds} seconds");
        }
        return time_builder.ToString();
    }

    private void InitializeShortcuts()
    {
        ConsoleKeyInfo info = Console.ReadKey(true);
        if (info.Modifiers.HasFlag(ConsoleModifiers.Control) && info.Key == ConsoleKey.P)
        {
            Pause();
        }
        else if (info.Modifiers.HasFlag(ConsoleModifiers.Control) && info.Key == ConsoleKey.S)
        {
            OpenSaveFile();
        }
        else if (info.Modifiers.HasFlag(ConsoleModifiers.Control) && info.Key == ConsoleKey.O)
        {
            OpenWorkspaceDirectory();
        }
        if (!stopping || !paused)
            InitializeShortcuts();
    }

    private void KillCurrent()
    {
        foreach (Process p in active_processes)
        {
            p?.Kill();
        }
    }

    private void Load()
    {
        using FileStream fs = new(settings_file, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        using StreamReader reader = new(fs);
        JObject json = null;

        json = JObject.Parse(reader.ReadToEnd());

        if (json != null)
        {
            concurrent = (int)json["concurrent_processes"];
            saved_bytes = (long)json["saved_bytes"];
            video_codec = (string)json["video_codec"];
            audio_codec = (string)json["audio_codec"];
            video_bitrate = (string)json["video_bitrate"];
            audio_bitrate = (string)json["audio_bitrate"];
            pixel_format = (string)json["pixel_format"];
            overwrite = (bool)json["overwrite"];
            processed = json["processed"].ToObject<List<ProcessedFile>>();
        }
    }

    private void OpenSaveFile()
    {
        Process p = new()
        {
            StartInfo = new()
            {
                FileName = settings_file,
                UseShellExecute = true,
                CreateNoWindow = false,

            }
        };

        p.Start();
    }

    private void OpenWorkspaceDirectory()
    {
        Process p = new()
        {
            StartInfo = new()
            {
                FileName = workspace_dir,
                UseShellExecute = true,
                CreateNoWindow = false,
            }
        };

        p.Start();
    }

    private void Pause()
    {
        paused = !paused;
        if (paused)
        {
            current_status = "Safe Exit is initiated. The current process will finish and then the program will exit!";
        }
        else
        {
            current_status = "";
        }
    }

    private Task ProcessFile(string file) => Task.Run(() =>
    {
        try
        {
            current_index++;
            Process process = null;
            active_processes.Add(process);
            List<double> recorded_speeds = new();
            long st = DateTime.Now.Ticks;
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
            StringBuilder ffoutput = new();
            bool killed = false;
            process = converter.Convert(tmp, (s, e) =>
            {
                string? content = e.Data;
                if (!string.IsNullOrWhiteSpace(content))
                {
                    ffoutput.AppendLine(content);
                }
                if (File.Exists(tmp) && !killed)
                {
                    FileInfo newFile = new(tmp);
                    if ((ulong)newFile.Length >= info.Size)
                    {
                        killed = true;
                        process.Kill();
                        string msg = $"Converted file is larger: {info.Filename}";
                        error_files.Add(msg);
                        Timer et = new(1000 * 20)
                        {
                            AutoReset = false,
                            Enabled = true
                        };
                        et.Elapsed += (s, e) =>
                        {
                            error_files.Remove(msg);
                            try
                            {
                                File.Delete(tmp);
                            }
                            catch
                            {
                            }
                        };
                        processed.Add(new(file, DateTime.Now.Ticks - st, (long)info.Size, (long)info.Size, info.Duration, recorded_speeds.Average(), false));
                        Save();
                        et.Start();
                    }
                }
            }, (s, e) =>
            {
                try
                {
                    if (!id.ContainsKey(file))
                    {
                        current_offset++;
                        id.Add(file, current_offset);
                    }

                    StringBuilder o = new();
                    o.Append($"[{new string(info.Filename.Take(18).ToArray()).Trim()}... ({id[file] + offset}/{(total_files)})] {e.Percentage:p2} | ");
                    double size = 50d;
                    for (int i = 0; i < size; i++)
                    {
                        if (e.Percentage * 100 >= i * (100 / size))
                        {
                            o.Append('=');
                        }
                        else
                        {
                            o.Append(' ');
                        }
                    }
                    recorded_speeds.Add(e.Speed);
                    speeds.Add(e.Speed);
                    o.Append($" | {e.Speed:n2}x Speed | {CLFileMath.AdjustedFileSize(new FileInfo(tmp).Length)} / {CLFileMath.AdjustedFileSize(info.Size)}");
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
            }, false);

            process.Start();
            process.BeginErrorReadLine();
            process.BeginOutputReadLine();
            process.WaitForExit();
            process.Close();
            if (output.Any() && output.ContainsKey(file))
                output.Remove(file);
            if (id.Any() && id.ContainsKey(file))
                id.Remove(file);

            if (!killed)
            {
                if (process.ExitCode == 0)
                {
                    long new_size = new FileInfo(tmp).Length;
                    if (new_size < fileInfo.Length)
                    {
                        long file_saved = fileInfo.Length - new_size;
                        saved_bytes += file_saved;
                        if (overwrite)
                        {
                            Task move_task = Task.Run(() =>
                               {
                                   moving_files.Add(file);
                                   File.Move(tmp, file, true);
                                   moving_files.Remove(file);
                               });
                            if (paused)
                                move_task.Wait();
                        }
                    }
                    processed.Add(new(file, DateTime.Now.Ticks - st, (long)info.Size, new_size, info.Duration, recorded_speeds.Average(), true));
                    est_time = (long)processed.Average(i => i.time) * files.Count;
                    reductions.Add(new_size / fileInfo.Length);
                    Save();
                }
                else
                {
                    error_files.Add(file);
                    Timer error_timeout = new(5000)
                    {
                        AutoReset = false,
                        Enabled = true
                    };
                    error_timeout.Elapsed += (s, e) =>
                    {
                        error_files.Remove(file);
                    };
                    error_timeout.Start();
                    string error_dir = Directory.CreateDirectory(Path.Combine(workspace_dir, "error")).FullName;
                    using FileStream fs = new(Path.Combine(error_dir, $"{info.Filename}_error.json"), FileMode.Create, FileAccess.Write, FileShare.None);
                    using StreamWriter writer = new(fs);
                    writer.Write(JsonConvert.SerializeObject(new
                    {
                        exit_code = process.ExitCode,
                        cmd = process.StartInfo.Arguments,
                        ffoutput = ffoutput.ToString()
                    }, Formatting.Indented));
                    File.Delete(tmp);
                }
            }
        }
        catch (Exception e)
        {
            string error_dir = Directory.CreateDirectory(Path.Combine(workspace_dir, "error")).FullName;
            using FileStream fs = new(Path.Combine(error_dir, $"error_{DateTime.Now.Ticks}.json"), FileMode.Create, FileAccess.Write, FileShare.None);
            using StreamWriter writer = new(fs);
            writer.Write(JsonConvert.SerializeObject(e, Formatting.Indented));
        }
    }).ContinueWith(a =>
    {
        if (!paused && !stopping && current_index < files.Count)
        {
            ProcessFile(files[current_index + 1]);
        }
        else
            Exit();
    });

    private void ProcessQueue()
    {
        for (int i = 0; i < concurrent; i++)
        {
            ProcessFile(files[i]);
        }
        InitializeShortcuts();
    }

    private void Save()
    {
        using FileStream fs = new(settings_file, FileMode.Create, FileAccess.ReadWrite, FileShare.ReadWrite);
        using StreamWriter writer = new(fs);
        writer.Write(JsonConvert.SerializeObject(new
        {
            concurrent_processes = concurrent,
            saved_bytes,
            video_codec,
            audio_codec,
            video_bitrate,
            audio_bitrate,
            pixel_format,
            overwrite,
            processed,
        }, Formatting.Indented));
    }

    private void UpdateScreen()
    {
        Console.Clear();
        Console.CursorTop = 0;
        Console.CursorLeft = 0;
        Console.ForegroundColor = ConsoleColor.Green;
        int path_length = 20;
        Console.Write("PROCESSING: ");
        Console.ForegroundColor = ConsoleColor.Cyan;
        string dir_name = new DirectoryInfo(Environment.CurrentDirectory).Name;
        Console.WriteLine(Environment.CurrentDirectory.Length >= path_length + dir_name.Length + 4 ? new string(Environment.CurrentDirectory.Take(path_length).ToArray()) + @"...\" + dir_name + @"\" : Environment.CurrentDirectory);
        StringBuilder builder = new();

        foreach (var o in output)
        {
            builder.AppendLine(o.Value);
        }

        Console.CursorTop = 1;
        Console.CursorLeft = 0;
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine(builder);
        Console.CursorTop = active_processes.Count + 2;
        Console.CursorLeft = 0;

        Console.ForegroundColor = ConsoleColor.Cyan;
        runtime = new(DateTime.Now.Ticks - start);
        Console.WriteLine($"Runtime: {GetTime(runtime)}");

        WriteStats();
        WriteEstamates();

        WriteMessages();

        Console.ResetColor();

        if (!stopping)
            update_screen_timer.Start();
    }

    private void WriteMessages()
    {
        Console.ForegroundColor = ConsoleColor.Blue;
        Console.WriteLine(current_status);

        foreach (string file in moving_files)
        {
            Console.WriteLine($"Overwriting {new FileInfo(file).Name}!");
        }

        Console.ForegroundColor = ConsoleColor.Red;
        foreach (string file in error_files)
        {
            Console.WriteLine($"Failed to Process {new FileInfo(file).Name}!");
        }
        Console.ResetColor();
    }

    void WriteStats()
    {
        if (saved_bytes > 0 && total_size > 0)
        {
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine("Statistics:");
            if (saved_bytes > 0)
                Console.WriteLine($"\t-Saved {CLFileMath.AdjustedFileSize(saved_bytes)}!");
            if (total_size > 0)
                Console.WriteLine($"\t-Total {CLFileMath.AdjustedFileSize(total_size)}!");
        }
        Console.ResetColor();
    }

    void WriteEstamates()
    {
        Console.ForegroundColor = ConsoleColor.Yellow;
        if (processed.Any() && est_time != 0)
        {
            Console.WriteLine($"Estamates: ");

            double average_speed = speeds.Average();
            double average_duration = durations.Average();

            double duration_seconds = (average_duration / average_speed * total_files) - runtime.TotalSeconds;
            double size_seconds = new TimeSpan(est_time - runtime.Ticks).TotalSeconds;

            Console.WriteLine($"\t-EST Time (size): {GetTime(TimeSpan.FromSeconds(size_seconds))}");
            Console.WriteLine($"\t-EST Time (duration): {GetTime(TimeSpan.FromSeconds(duration_seconds))}");
            Console.WriteLine($"\t-EST Time (average): {GetTime(TimeSpan.FromSeconds((duration_seconds + size_seconds) / 2))}");
            Console.WriteLine($"\t-EST Savings: {CLFileMath.AdjustedFileSize(reductions.Average() * total_size)}");
        }
        Console.ResetColor();
    }
}
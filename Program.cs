using Chase.FFmpeg.Converters;
using Chase.FFmpeg.Downloader;
using Chase.FFmpeg.Extra;
using Chase.FFmpeg.Info;
using CLMath;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Diagnostics;
using System.Reflection;
using System.Text;
using Timer = System.Timers.Timer;

namespace BatchProcessFFmpeg;


internal class Program
{
    int current_index;
    string tmp_dir;
    int concurrent = 3;
    long saved_bytes = 0;
    string video_codec = "h264";
    string audio_codec = "aac";
    string video_bitrate = "";
    string audio_bitrate = "";
    string pixel_format = "yuv420p";
    bool overwrite = true;
    bool paused = false;
    string workspace_dir;
    string settings_file;
    long start;

    List<string> files = new();
    Dictionary<string, string> output = new();
    Dictionary<string, int> id = new();
    List<string> moving_files = new();
    List<string> error_files = new();
    List<Process> active_processes = new();
    List<string> processed = new();

    Program(string[] args)
    {
        InitializeShortcuts();

        Timer timer = new(1000)
        {
            AutoReset = true,
            Enabled = true,
        };
        timer.Elapsed += (s, e) => UpdateScreen();
        timer.Start();
        string exe_dir = Environment.CurrentDirectory;
        if (args.Any())
        {
            Environment.CurrentDirectory = Path.GetFullPath(args[0]);
        }

        exe_dir = Directory.CreateDirectory(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LFInteractive", "Batch Process FFmpeg")).FullName;

        workspace_dir = Directory.CreateDirectory(Path.Combine(exe_dir, Environment.CurrentDirectory.Replace(Path.DirectorySeparatorChar, '_').Replace(":", ""))).FullName;
        settings_file = Path.Combine(workspace_dir, $"settings.json");
        start = DateTime.Now.Ticks;
        current_index = processed?.Count ?? 0;
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
        Console.WriteLine($"[ {DateTime.Now:MM/dd/yyyy - h:mmtt} ] Checking FFmpeg...");
        FFmpegDownloader.Instance.GetLatest(Path.Combine(exe_dir, "ffmpeg")).Wait();
        Console.WriteLine($"[ {DateTime.Now:MM/dd/yyyy - h:mmtt} ] Scanning Files...");
        Console.ResetColor();
        try
        {
            files = FFVideoUtility.GetFiles(Environment.CurrentDirectory, true).Where(i => !processed.Contains(i)).OrderBy(i => new FileInfo(i).Length).Reverse().ToList();
        }
        catch
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"[ {DateTime.Now:MM/dd/yyyy - h:mmtt} ] Error while scanning for files... Trying again!");
            Console.ResetColor();
            _ = new Program(new string[] { Environment.CurrentDirectory });
            return;
        }

        AppDomain.CurrentDomain.ProcessExit += (s, e) => Cleanup();
        Console.CancelKeyPress += (s, e) => Cleanup();

        ProcessQueue();

    }

    void Cleanup()
    {
        Console.ResetColor();
        if (Directory.Exists(tmp_dir))
            Directory.Delete(tmp_dir, true);
    }

    Task InitializeShortcuts() => Task.Run(() =>
    {
        while (true)
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
        }
    });

    void UpdateScreen()
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

        Console.ForegroundColor = ConsoleColor.Yellow;
        foreach (string file in moving_files)
        {
            Console.WriteLine($"Overwriting {new FileInfo(file).Name}!");
        }
        Console.ForegroundColor = ConsoleColor.Red;
        foreach (string file in error_files)
        {
            Console.WriteLine($"Failed to Process {new FileInfo(file).Name}!");
        }

        if (paused)
        {
            Console.ForegroundColor = ConsoleColor.Blue;
            Console.WriteLine($"Process is PAUSED! Press enter to unpause...");
        }

        Console.ResetColor();
    }

    void OpenSaveFile()
    {
        Process.Start(new ProcessStartInfo()
        {
            FileName = settings_file,
            UseShellExecute = true,
            CreateNoWindow = true
        })
    }

    static void Main(string[] args)
    {
        _ = new Program(args);
    }


    void ProcessQueue()
    {
        Parallel.ForEach(files, new() { MaxDegreeOfParallelism = concurrent }, file =>
        {
            if (paused)
                return;
            ProcessFile(file);
        });
    }

    void ProcessFile(string file)
    {
        try
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
            StringBuilder ffoutput = new();
            Process process = null;
            active_processes.Add(process);
            process = converter.Convert(tmp, (s, e) =>
            {
                string? content = e.Data;
                if (!string.IsNullOrWhiteSpace(content))
                {
                    ffoutput.AppendLine(content);
                }
                FileInfo newFile = new(tmp);
                if ((ulong)newFile.Length >= info.Size)
                {
                    error_files.Add($"Converted file is larger: {info.Filename}");
                    process?.Kill();
                    processed.Add(file);
                    files.Remove(file);
                    Save();
                }

            }, (s, e) =>
            {
                try
                {
                    if (!id.ContainsKey(file))
                    {
                        current_index++;
                        id.Add(file, current_index);
                    }

                    StringBuilder o = new();
                    o.Append($"[{new string(info.Filename.Take(18).ToArray()).Trim()}... ({id[file]}/{(files.Count() + 1)})] {(e.Percentage / 100):p2} | ");
                    double size = 50d;
                    for (int i = 0; i < size; i++)
                    {
                        if (e.Percentage >= i * (100 / size))
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
                    if (overwrite)
                    {
                        Task.Run(() =>
                        {
                            moving_files.Add(file);
                            File.Move(tmp, file, true);
                            moving_files.Remove(file);
                        });

                    }
                }
                else
                {
                    File.Delete(tmp);
                }
                processed.Add(file);
                files.Remove(file);
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
        catch (Exception e)
        {
            string error_dir = Directory.CreateDirectory(Path.Combine(workspace_dir, "error")).FullName;
            using FileStream fs = new(Path.Combine(error_dir, $"error_{DateTime.Now.Ticks}.json"), FileMode.Create, FileAccess.Write, FileShare.None);
            using StreamWriter writer = new(fs);
            writer.Write(JsonConvert.SerializeObject(e, Formatting.Indented));
        }
    }

    void Save()
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
            processed
        }, Formatting.Indented));
    }

    void Load()
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
            processed = json["processed"].ToObject<List<string>>();
        }
    }

    void Pause()
    {
        paused = true;
        KillCurrent();
        Console.ReadLine();

        Unpause();
    }

    void Unpause()
    {
        paused = false;
        ProcessQueue();
    }

    void KillCurrent()
    {
        foreach (Process p in active_processes)
        {
            p?.Kill();
        }
    }

}
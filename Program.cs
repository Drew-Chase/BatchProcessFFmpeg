// LFInteractive LLC. 2021-2024

using System.Diagnostics;
using System.Reflection;
using System.Text;
using BatchProcessFFmpeg.Models;
using Chase.FFmpeg.Converters;
using Chase.FFmpeg.Downloader;
using Chase.FFmpeg.Extra;
using Chase.FFmpeg.Info;
using CLMath;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Timer = System.Timers.Timer;

namespace BatchProcessFFmpeg;

//record ProcessedFile(string file, long time, long original_size, long new_size, TimeSpan video_duration, double average_speed, bool successful);

internal class Program
{
    private readonly List<Process> active_processes = [];
    private readonly List<double> durations = [];
    private readonly List<string> error_files = [];
    private readonly string export_file;

    private readonly Timer export_timer = new(TimeSpan.FromSeconds(20).TotalMilliseconds)
    {
        AutoReset = false,
        Enabled = true
    };

    private readonly Dictionary<string, int> id = [];
    private readonly List<string> moving_files = [];
    private readonly Dictionary<string, string> output = [];
    private readonly List<long> process_times = [];
    private readonly string[] processing_dirs;
    private readonly List<double> reductions = [];
    private readonly string settings_file;
    private readonly List<double> speeds = [];
    private readonly long start;
    private readonly string tmp_dir;

    private readonly Timer update_screen_timer = new(500)
    {
        AutoReset = false,
        Enabled = true
    };

    private readonly string workspace_dir;
    private string audio_bitrate = "";
    private string audio_codec = "aac";
    private int concurrent = 3;
    private int current_index;
    private int current_offset;
    private readonly List<string> current_status = new();
    private long est_time;
    private TimeSpan export_creation;
    private List<string> files = new();
    private bool needs_reeval;
    private int offset;
    private bool overwrite = true;
    private bool paused;
    private string pixel_format = "yuv420p";
    private List<ProcessedFile> processed = new();
    private TimeSpan runtime;
    private long saved_bytes;
    private bool stopping;
    private int total_files;
    private long total_size;
    private string video_bitrate = "";
    private string video_codec = "h264";
    private FileSystemWatcher[] watchers;

    private Program(string[] args)
    {
        if (args.Any())
        {
            processing_dirs = new string[args.Length];
            for (var i = 0; i < args.Length; i++)
                if (Directory.Exists(args[i]))
                {
                    processing_dirs[i] = args[i];
                }
                else
                {
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.Error.WriteLine($"Argument is not a directory: \"{args[i]}\"");
                    Console.ResetColor();
                    Environment.Exit(1);
                    return;
                }
        }

        var exe_path = Assembly.GetExecutingAssembly()?.Location ?? "";
        if (string.IsNullOrWhiteSpace(exe_path))
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.Error.WriteLine("Could not find executable path!");
            Console.ResetColor();
            Environment.Exit(1);
            return;
        }

        var exe_dir = Directory.GetParent(exe_path)?.FullName ?? "";

        start = DateTime.Now.Ticks;

        UpdateScreen();
        update_screen_timer.Elapsed += (s, e) => UpdateScreen();

        workspace_dir = Directory.CreateDirectory(Path.Combine(exe_dir, string.Join("--", processing_dirs).Replace(Path.DirectorySeparatorChar, '_').Replace(":", ""))).FullName;
        settings_file = Path.Combine(workspace_dir, "settings.json");
        export_file = Path.Combine(workspace_dir, "export.json");
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
        Watch();

        Console.ForegroundColor = ConsoleColor.Yellow;
        current_status.Add("Checking FFmpeg...");
        FFmpegDownloader.Instance.GetLatest(Path.Combine(exe_dir, "ffmpeg")).Wait();
        current_status.Remove("Checking FFmpeg...");
        Console.ResetColor();
        Task.Run(() => { InitializeShortcuts(); });
        GetFiles();
        if (files.Any())
        {
            current_status.Clear();
            ProcessQueue();
        }
    }

    public void Dispose()
    {
        Exit();
    }

    private static string CleanFilename(string filename)
    {
        StringBuilder builder = new();
        builder.Append(filename);
        List<char> chars = [.. Path.GetInvalidFileNameChars(), .. Path.GetInvalidPathChars()];
        foreach (var c in chars) builder = builder.Replace(c, '_');
        return builder.ToString();
    }

    private static string GetTime(TimeSpan span)
    {
        StringBuilder time_builder = new();
        if (span.Days > 0) time_builder.Append($"{span.Days} days ");
        if (span.Hours > 0) time_builder.Append($"{span.Hours} hours ");
        if (span.Minutes > 0) time_builder.Append($"{span.Minutes} minutes ");
        if (span.Seconds > 0) time_builder.Append($"{span.Seconds} seconds");
        return time_builder.ToString();
    }

    private static void Main(string[] args)
    {
        _ = new Program(args);
    }

    private string Copy(string from, string to)
    {
        FileInfo fileInfo = new(from);
        current_status.Add($"Copying {fileInfo.Name}");
        var length = fileInfo.Length;
        var buffer = new byte[1024 * 1024];
        using (FileStream dest = new(to, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            using FileStream source = new(from, FileMode.Open, FileAccess.Read, FileShare.None);
            long totalBytes = 0;
            var currentBlockSize = 0;

            while ((currentBlockSize = source.Read(buffer, 0, buffer.Length)) > 0)
            {
                totalBytes += currentBlockSize;
                var percentage = (double)totalBytes / length;
                dest.Write(buffer, 0, currentBlockSize);
                //current_status.Remove(current_status.First(i => i.StartsWith($"Copying {fileInfo.Name}")));
                //current_status.Add($"Copying {fileInfo.Name} {percentage:P2}");
                //current_status = current_status.OrderBy(i => i).ToList();

                try
                {
                    StringBuilder o = new();
                    var name = fileInfo.Name;
                    var l = (int)(Console.WindowWidth * .35);
                    if (name.Length > l + 4) name = $"{new string(fileInfo.Name.Take(l / 2).ToArray()).Trim()}...{new string(fileInfo.Name.TakeLast(l / 2).ToArray()).Trim()}";
                    var bf = $"[{name}] {percentage:p2} | ";
                    var content_size = bf.Length;
                    o.Append(bf);
                    double size = Console.WindowWidth - content_size - 10;
                    for (var i = 0; i < size; i++)
                        if (percentage * 100 >= i * (100 / size))
                            o.Append('=');
                        else
                            o.Append(' ');
                    output[from] = o.ToString();
                }
                catch
                {
                    //Console.WriteLine();
                }
            }
        }

        File.Copy(from, to, true);
        current_status.Remove($"Copying {fileInfo.Name}");
        return to;
    }

    private void Error(string message, string name = "error")
    {
        name = CleanFilename(name);
        var error_dir = Directory.CreateDirectory(Path.Combine(workspace_dir, "error")).FullName;
        using FileStream fs = new(Path.Combine(error_dir, $"{name}_{DateTime.Now.Ticks}.json"), FileMode.Create, FileAccess.Write, FileShare.None);
        using StreamWriter writer = new(fs);
        writer.Write(message);
    }

    private void Error(Exception e, string name = "error")
    {
        Error(JsonConvert.SerializeObject(e, Formatting.Indented), name);
    }

    private void Exit(int attempts = 0)
    {
        stopping = true;
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine("Exiting!!!!");
        if (attempts > 0) Console.WriteLine($"Attempt: {attempts + 1}");
        Console.ResetColor();

        try
        {
            foreach (var watcher in watchers) watcher?.Dispose();
            KillCurrent();
            if (Directory.Exists(tmp_dir))
                Directory.Delete(tmp_dir, true);
        }
        catch
        {
            Thread.Sleep(1000 * 5);
            Exit(attempts + 1);
            return;
        }

        Console.WriteLine("DONE!");
    }

    private void Export()
    {
        try
        {
            current_status.Add("Exporting Stats...");
            using FileStream fs = new(export_file, FileMode.Create, FileAccess.Write, FileShare.Read);
            using StreamWriter writer = new(fs);
            writer.Write(JsonConvert.SerializeObject(new
            {
                Time = export_creation,
                ReEvaluate = needs_reeval,
                Size = total_size,
                Saved_Bytes = saved_bytes,
                processed,
                files
            }, Formatting.Indented));
        }
        catch (Exception e)
        {
            current_status.Remove("Exporting Stats...");
            Error(e);
        }

        current_status.Remove("Exporting Stats...");
        export_timer.Start();
    }

    private void GetFiles()
    {
        files = [.. Import()];
        if (!files.Any())
        {
            current_status.Add("Scanning for Files...");
            try
            {
                total_size = 0;
                foreach (var dir in processing_dirs) files.AddRange(FFVideoUtility.GetFiles(dir, true));
                needs_reeval = true;
            }
            catch (Exception e)
            {
                Console.WriteLine(JsonConvert.SerializeObject(e, Formatting.Indented));
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine("Error while scanning for files... Trying again!");
                Console.ResetColor();
                _ = new Program([Environment.CurrentDirectory]);
                return;
            }
        }

        current_status.Remove("Scanning for Files...");
        if (!files.Any())
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine("No files found!");

            Environment.Exit(2);
        }
        else
        {
            total_files = files.Count;

            if (processed.Any())
            {
                current_status.Add("Parsing Stats...");

                foreach (var process in processed)
                {
                    files.Remove(process.file);
                    speeds.Add(process.average_speed);
                    reductions.Add((double)process.original_size / process.new_size);
                    process_times.Add(process.time);
                    durations.Add(process.video_duration.TotalSeconds);
                }
            }

            offset = total_files - files.Count;
            AppDomain.CurrentDomain.ProcessExit += (s, e) => Exit();
            Console.CancelKeyPress += (s, e) => Exit();

            if (processed != null && processed.Any() && files.Any()) est_time = (long)processed.Average(i => i.time) * files.Count;
            current_status.Remove("Parsing Stats...");
            if (needs_reeval)
            {
                total_size = 0;
                current_status.Add("Organizing by from size...");
                files = files.Where(i => File.Exists(i)).OrderByDescending(i =>
                {
                    FileInfo info = new(i);
                    total_size += info.Length;
                    return info.Length;
                }).ToList();
                current_status.Remove("Organizing by from size...");
                needs_reeval = false;
            }
        }
    }

    private string[] Import()
    {
        current_status.Add("Importing Stats...");
        if (File.Exists(export_file))
        {
            using FileStream fs = new(export_file, FileMode.Open, FileAccess.Read, FileShare.None);
            using StreamReader reader = new(fs);
            var json = JObject.Parse(reader.ReadToEnd());
            var creation_time = json["Time"]?.ToObject<TimeSpan>() ?? TimeSpan.MinValue;
            TimeSpan current_time = new(start);
            export_creation = new TimeSpan(current_time.Ticks - creation_time.Ticks);
            total_size = json["Size"]?.ToObject<long>() ?? 0;
            needs_reeval = json["ReEvaluate"]?.ToObject<bool>() ?? false;
            saved_bytes = json["Saved_Bytes"]?.ToObject<long>() ?? 0;
            processed = json["processed"]?.ToObject<List<ProcessedFile>>() ?? new List<ProcessedFile>();
            if (export_creation.TotalDays < 5)
                return json["files"]?.ToObject<string[]>() ?? Array.Empty<string>();
            export_creation = new TimeSpan(start);
        }

        export_creation = new TimeSpan(start);
        current_status.Remove("Importing Stats...");
        return Array.Empty<string>();
    }

    private async Task InitializeShortcuts()
    {
        var info = Console.ReadKey(true);
        if (info.Modifiers.HasFlag(ConsoleModifiers.Control) && info.Key == ConsoleKey.P)
            Pause();
        else if (info.Modifiers.HasFlag(ConsoleModifiers.Control) && info.Key == ConsoleKey.S)
            OpenSaveFile();
        else if (info.Modifiers.HasFlag(ConsoleModifiers.Control) && info.Key == ConsoleKey.O)
            OpenWorkspaceDirectory();
        else if (info.Modifiers.HasFlag(ConsoleModifiers.Control) && info.Key == ConsoleKey.I) needs_reeval = true;
        if (!stopping || !paused)
            await InitializeShortcuts();
    }

    private void KillCurrent()
    {
        foreach (var p in active_processes)
        {
            p?.Kill();
            p?.Close();
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
            video_codec = (string)json["video_codec"];
            audio_codec = (string)json["audio_codec"];
            video_bitrate = (string)json["video_bitrate"];
            audio_bitrate = (string)json["audio_bitrate"];
            pixel_format = (string)json["pixel_format"];
            overwrite = (bool)json["overwrite"];
        }
    }

    private void OpenSaveFile()
    {
        Process p = new()
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = settings_file,
                UseShellExecute = true,
                CreateNoWindow = false
            }
        };

        p.Start();
    }

    private void OpenWorkspaceDirectory()
    {
        Process p = new()
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = workspace_dir,
                UseShellExecute = true,
                CreateNoWindow = false
            }
        };

        p.Start();
    }

    private void Pause()
    {
        paused = !paused;
        if (paused)
            current_status.Add("Safe Exit is initiated. The current process will finish and then the program will exit!");
        else
            current_status.Remove("Safe Exit is initiated. The current process will finish and then the program will exit!");
    }

    private Task ProcessFile(string file)
    {
        return Task.Run(() =>
        {
            try
            {
                current_index++;
                Process process = null;
                active_processes.Add(process);
                List<double> recorded_speeds = new();
                var st = DateTime.Now.Ticks;

                FileInfo fileInfo = new(file);

                var og_file = file;
                output.Add(og_file, "");
                //file = Copy(file, Path.Combine(tmp_dir, $"og_{fileInfo.Name}"));

                FFMediaInfo info = new(file);
                var converter = FFMuxedConverter.SetMedia(info);
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
                converter.AddCustomPreInputOption("-hide_banner");
                converter.AddCustomPostInputOption("-map 0:v:0"); // Map only the first video stream
                converter.AddCustomPostInputOption("-map 0:a"); // Map all audio streams
                converter.AddCustomPostInputOption("-c:s copy");
                converter.AddCustomPostInputOption("-ac 2");

                var tmp = Path.Combine(tmp_dir, $"{info.Filename}_tmp{fileInfo.Extension}");
                StringBuilder ffoutput = new();
                var killed = false;
                process = converter.Convert(tmp, (s, e) =>
                {
                    var content = e.Data;
                    if (!string.IsNullOrWhiteSpace(content)) ffoutput.AppendLine(content);
                    if (File.Exists(tmp) && !killed)
                    {
                        FileInfo newFile = new(tmp);
                        if ((ulong)newFile.Length >= info.Size)
                        {
                            killed = true;
                            process.Kill();
                            var msg = $"Converted name is larger: {info.Filename}";
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
                            var time_to_complete = DateTime.Now.Ticks - st;
                            process_times.Add(time_to_complete);
                            processed.Add(new ProcessedFile
                            {
                                file = file,
                                time = time_to_complete,
                                original_size = (long)info.Size,
                                new_size = (long)info.Size,
                                video_duration = info.Duration,
                                average_speed = recorded_speeds.Average(),
                                successful = false
                            });
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
                        if (double.IsInfinity(e.Percentage)) Console.WriteLine();
                        var name = info.Filename;
                        var length = (int)(Console.WindowWidth * .2);
                        if (name.Length > length + 4) name = $"{new string(info.Filename.Take(length / 2).ToArray()).Trim()}...{new string(info.Filename.TakeLast(length / 2).ToArray()).Trim()}";
                        var bf = $"[{name} ({id[file] + offset}/{total_files})] {e.Percentage:p2} | ";
                        var pf = $" | {e.Speed:n2}x Speed | {CLFileMath.AdjustedFileSize(new FileInfo(tmp).Length)} / {CLFileMath.AdjustedFileSize(info.Size)}";
                        var content_size = bf.Length + pf.Length;
                        o.Append(bf);
                        double size = Console.WindowWidth - content_size - 10;
                        for (var i = 0; i < size; i++)
                            if (e.Percentage * 100 >= i * (100 / size))
                                o.Append('=');
                            else
                                o.Append(' ');
                        recorded_speeds.Add(e.Speed);
                        speeds.Add(e.Speed);
                        o.Append(pf);
                        if (!output.ContainsKey(og_file))
                            output.Add(og_file, o.ToString());
                        else
                            output[og_file] = o.ToString();
                    }
                    catch
                    {
                        Console.WriteLine();
                    }
                }, false);

                process.Start();
                process.BeginErrorReadLine();
                process.BeginOutputReadLine();
                process.WaitForExit();

                if (process != null && process.HasExited)
                    active_processes.Remove(process);

                if (output.Any() && output.ContainsKey(og_file))
                    output.Remove(og_file);
                if (id.Any() && id.ContainsKey(file))
                    id.Remove(file);

                if (!killed && !stopping)
                {
                    if (process.ExitCode == 0)
                    {
                        var new_size = new FileInfo(tmp).Length;
                        if (new_size < fileInfo.Length)
                        {
                            var file_saved = fileInfo.Length - new_size;
                            saved_bytes += file_saved;
                            if (overwrite)
                                try
                                {
                                    Task? move_task = null;
                                    move_task = new Task(() =>
                                    {
                                        try
                                        {
                                            moving_files.Add(og_file);
                                            File.Move(tmp, og_file, true);
                                            moving_files.Remove(og_file);
                                        }
                                        catch
                                        {
                                            moving_files.Remove(file);
                                            Thread.Sleep(1000 * 5);
                                            move_task?.Start();
                                        }
                                    });
                                    try
                                    {
                                        move_task.Start();
                                        if (paused || stopping || current_index >= files.Count)
                                            move_task.Wait();
                                    }
                                    catch
                                    {
                                    }
                                }
                                catch (Exception e)
                                {
                                    Error(e);
                                }
                        }

                        processed.Add(new ProcessedFile
                        {
                            file = file,
                            time = DateTime.Now.Ticks - st,
                            original_size = (long)info.Size,
                            new_size = new_size,
                            video_duration = info.Duration,
                            average_speed = recorded_speeds.Average(),
                            successful = true
                        });
                        est_time = (long)processed.Average(i => i.time) * files.Count;
                        reductions.Add(new_size / fileInfo.Length);
                        Save();

                        if (File.Exists(file))
                            File.Delete(file);
                        if (File.Exists(tmp))
                            File.Delete(tmp);
                    }
                    else
                    {
                        error_files.Add(file);
                        Timer error_timeout = new(5000)
                        {
                            AutoReset = false,
                            Enabled = true
                        };
                        error_timeout.Elapsed += (s, e) => { error_files.Remove(file); };
                        error_timeout.Start();

                        Error(JsonConvert.SerializeObject(new
                        {
                            exit_code = process.ExitCode,
                            cmd = process.StartInfo.Arguments,
                            ffoutput = ffoutput.ToString()
                        }, Formatting.Indented), info.Filename);

                        File.Delete(tmp);
                    }
                }

                process?.Close();
            }
            catch (Exception e)
            {
                Error(e);
            }
        }).ContinueWith(a =>
        {
            if (!paused && !stopping && current_index < files.Count) ProcessFile(files[current_index + 1]).Wait();
        });
    }

    private void ProcessQueue()
    {
        var pro = new Task[concurrent];
        for (var i = 0; i < concurrent; i++) pro[i] = ProcessFile(files[i]);
        export_timer.Elapsed += (s, e) => Export();
        Export();
        Task.WaitAll(pro);
    }

    private void Save()
    {
        using FileStream fs = new(settings_file, FileMode.Create, FileAccess.ReadWrite, FileShare.ReadWrite);
        using StreamWriter writer = new(fs);
        writer.Write(JsonConvert.SerializeObject(new
        {
            concurrent_processes = concurrent,
            video_codec,
            audio_codec,
            video_bitrate,
            audio_bitrate,
            pixel_format,
            overwrite
        }, Formatting.Indented));
    }

    private Task Watch()
    {
        return Task.Run(() =>
        {
            watchers = new FileSystemWatcher[processing_dirs.Length];
            for (var i = 0; i < processing_dirs.Length; i++)
            {
                watchers[i] = new FileSystemWatcher
                {
                    Path = processing_dirs[i],
                    NotifyFilter = NotifyFilters.CreationTime | NotifyFilters.DirectoryName | NotifyFilters.FileName,
                    Filter = "*.*",
                    IncludeSubdirectories = true,
                    EnableRaisingEvents = true
                };
                watchers[i].Created += handler;
                watchers[i].Deleted += handler;
            }

            void handler(object s, FileSystemEventArgs e)
            {
                try
                {
                    if (!processed.Any(i => i.file.Equals(e.FullPath)) && FFVideoUtility.video_extension.Contains(new FileInfo(e.FullPath).Extension.Trim('.')))
                    {
                        if (e.ChangeType == WatcherChangeTypes.Deleted && files.Contains(e.FullPath)) files.Remove(e.FullPath);
                        if (e.ChangeType == WatcherChangeTypes.Created && !files.Contains(e.FullPath)) files.Add(e.FullPath);

                        current_status.Add($"Detected filesystem change!\nFile: \"{e.FullPath}\"\nChange Type: {e.ChangeType}");
                        needs_reeval = true;
                        Thread.Sleep(1000 * 10);
                        current_status.Remove($"Detected filesystem change!\nFile: \"{e.FullPath}\"\nChange Type: {e.ChangeType}");
                    }
                }
                catch (Exception ex)
                {
                    Error(ex);
                }
            }
        });
    }

    private void UpdateScreen()
    {
        // Build all strings first before any console operations
        var screenBuilder = new StringBuilder();
        var currentColor = Console.ForegroundColor;
        try
        {
            // Processing section
            screenBuilder.AppendLine("PROCESSING: ");
            var path_length = 20;
            foreach (var dir in processing_dirs)
            {
                var dir_name = new DirectoryInfo(dir).Name;
                screenBuilder.AppendLine("- " + (dir.Length >= path_length + dir_name.Length + 4
                    ? new string(dir.Take(path_length).ToArray()) + @"...\" + dir_name + @"\"
                    : dir));
            }

            // Output section
            foreach (var o in output)
            {
                screenBuilder.AppendLine(o.Value.StartsWith("[og_") ? "[" + o.Value[4..] : o.Value);
            }

            // Runtime section
            runtime = new TimeSpan(DateTime.Now.Ticks - start);
            screenBuilder.AppendLine($"Runtime: {GetTime(runtime)}\n");

            // Status flags section
            screenBuilder.Append("Needs Refresh: ");
            screenBuilder.AppendLine(needs_reeval.ToString());

            screenBuilder.Append("Overwrite: ");
            screenBuilder.AppendLine(overwrite.ToString());
            screenBuilder.AppendLine();

            // Get stats, estimates and messages content
            var statsContent = GetStatsContent();
            var estimatesContent = GetEstimatesContent();
            var messagesContent = GetMessagesContent();

            // Now clear and write everything at once
            Console.Clear();
            Console.CursorTop = 0;
            Console.CursorLeft = 0;

            // Write processing section
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine("PROCESSING: ");
            Console.ForegroundColor = ConsoleColor.Cyan;
            var lines = screenBuilder.ToString().Split(Environment.NewLine);
            foreach (var line in lines.Skip(1).Take(processing_dirs.Length))
            {
                Console.WriteLine(line);
            }

            // Write output section
            Console.CursorTop = processing_dirs.Length + 2;
            Console.CursorLeft = 0;
            Console.ForegroundColor = ConsoleColor.Yellow;
            foreach (var line in lines.Skip(processing_dirs.Length + 1).Take(output.Count))
            {
                Console.WriteLine(line);
            }

            // Write runtime
            Console.CursorTop = concurrent + 5;
            Console.CursorLeft = 0;
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine($"Runtime: {GetTime(runtime)}\n");

            // Write status flags
            Console.ForegroundColor = ConsoleColor.DarkYellow;
            Console.Write("Needs Refresh: ");
            Console.ForegroundColor = needs_reeval ? ConsoleColor.Green : ConsoleColor.Red;
            Console.WriteLine(needs_reeval);

            Console.ForegroundColor = ConsoleColor.DarkYellow;
            Console.Write("Overwrite: ");
            Console.ForegroundColor = overwrite ? ConsoleColor.Green : ConsoleColor.Red;
            Console.WriteLine(overwrite);
            Console.WriteLine();

            // Write cached content
            Console.ForegroundColor = ConsoleColor.DarkCyan;
            Console.Write(statsContent);
            Console.Write(estimatesContent);
            Console.ForegroundColor = ConsoleColor.Blue;
            Console.Write(messagesContent);
        }
        catch (Exception e)
        {
            Error(e);
        }

        Console.ForegroundColor = currentColor;
        if (!stopping)
            update_screen_timer.Start();
    }

    private string GetStatsContent()
    {
        var statBuilder = new StringBuilder();
        try
        {
            statBuilder.AppendLine("Statistics:");
            if (saved_bytes > 0)
                statBuilder.AppendLine($"\t-Total Saved:        {CLFileMath.AdjustedFileSize(saved_bytes)}!");
            if (total_size > 0)
                statBuilder.AppendLine($"\t-Total Remaining:    {CLFileMath.AdjustedFileSize(total_size)}!");
            statBuilder.AppendLine();
            if (speeds.Count > 4)
                statBuilder.AppendLine($"\t-Average Speed:      {speeds.Average():n2}x!");
            if (durations.Count > 4)
                statBuilder.AppendLine($"\t-Average Duration:   {GetTime(TimeSpan.FromSeconds(durations.Average()))}!");
            if (reductions.Count > 4)
                statBuilder.AppendLine($"\t-Average Savings:    {reductions.Average():p2}!");
            if (process_times.Count > 4)
                statBuilder.AppendLine($"\t-Average Time:       {GetTime(TimeSpan.FromTicks((long)process_times.Average()))}!");

            statBuilder.AppendLine();
        }
        catch (Exception e)
        {
            Error(e);
        }

        return statBuilder.ToString();
    }

    private string GetEstimatesContent()
    {
        var estBuilder = new StringBuilder();
        try
        {
            if (processed.Count > 2 && durations.Count > 2 && speeds.Count > 2 && est_time != 0)
            {
                estBuilder.AppendLine("Estimates:");
                var duration_seconds = durations.Average() / speeds.Average() * total_files - runtime.TotalSeconds;
                var size_seconds = new TimeSpan(est_time - runtime.Ticks).TotalSeconds;
                estBuilder.AppendLine($"\t-EST Time:           {GetTime(TimeSpan.FromSeconds((duration_seconds + size_seconds) / 2))}");
                estBuilder.AppendLine($"\t-EST Savings:        {CLFileMath.AdjustedFileSize(reductions.Average() * total_size)}");
                estBuilder.AppendLine();
            }
        }
        catch (Exception e)
        {
            Error(e);
        }

        return estBuilder.ToString();
    }

    private string GetMessagesContent()
    {
        var messageBuilder = new StringBuilder();
        try
        {
            foreach (var item in current_status)
                messageBuilder.AppendLine(item);

            foreach (var file in moving_files)
                messageBuilder.AppendLine($"Overwriting {new FileInfo(file).Name}!");

            foreach (var file in error_files)
                messageBuilder.AppendLine($"Failed to Process {new FileInfo(file).Name}!");
        }
        catch (Exception e)
        {
            Error(e);
        }

        return messageBuilder.ToString();
    }
}
using Chase.FFmpeg.Converters;
using Chase.FFmpeg.Downloader;
using Chase.FFmpeg.Extra;
using Chase.FFmpeg.Info;
using CLMath;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
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
        exe_dir = Directory.CreateDirectory(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LFInteractive", "Batch Process FFmpeg")).FullName;

        string workspace_dir = Directory.CreateDirectory(Path.Combine(exe_dir, Environment.CurrentDirectory.Replace(Path.DirectorySeparatorChar, '_').Replace(":", ""))).FullName;
        string settings_file = Path.Combine(workspace_dir, $"settings.json");

        int concurrent = 3;
        long saved_bytes = 0;
        string video_codec = "h264";
        string audio_codec = "aac";
        string video_bitrate = "";
        string audio_bitrate = "";
        string pixel_format = "yuv420p";
        bool overwrite = true;
        List<string> processed = new();

        using (FileStream fs = new(settings_file, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
        {
            JObject json;
            if (File.Exists(settings_file))
            {
                using StreamReader reader = new(fs);
                json = JObject.Parse(reader.ReadToEnd());
            }
            else
            {
                using StreamWriter writer = new(fs);
                json = JObject.FromObject(new
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
                });
                writer.Write(JsonConvert.SerializeObject(json, Formatting.Indented));
            }

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

        using (FileStream fs = new(settings_file, FileMode.Create, FileAccess.Write, FileShare.None))
        {
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

        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine($"[ {DateTime.Now:MM/dd/yyyy - h:mmtt} ] Checking FFmpeg...");
        FFmpegDownloader.Instance.GetLatest(Path.Combine(exe_dir, "ffmpeg")).Wait();
        Console.WriteLine($"[ {DateTime.Now:MM/dd/yyyy - h:mmtt} ] Scanning Files...");
        Console.ResetColor();
        string[] files = Array.Empty<string>();
        try
        {
            files = FFVideoUtility.GetFiles(Environment.CurrentDirectory, true).OrderBy(i => new FileInfo(i).Length).Reverse().ToArray();
        }
        catch
        {
            Main(new string[] { Environment.CurrentDirectory });
        }

        Dictionary<string, string> output = new();
        Dictionary<string, int> id = new();
        List<string> moving_files = new();
        List<string> error_files = new();

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

            Console.ResetColor();
        };
        timer.Start();

        int index = 0;

        string tmp_dir = Directory.CreateDirectory(Path.Combine(workspace_dir, "tmp")).FullName;
        if (Directory.Exists(tmp_dir))
            Directory.Delete(tmp_dir, true);
        Directory.CreateDirectory(tmp_dir);

        AppDomain.CurrentDomain.ProcessExit += (s, e) =>
        {
            if (Directory.Exists(tmp_dir))
                Directory.Delete(tmp_dir, true);
        };
        Console.CancelKeyPress += (s, e) =>
        {
            if (Directory.Exists(tmp_dir))
                Directory.Delete(tmp_dir, true);
        };

        Parallel.ForEach(files, new() { MaxDegreeOfParallelism = concurrent }, file =>
        {
            if (!processed.Contains(file))
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
                    var process = converter.Convert(tmp, (s, e) =>
                    {
                        string? content = e.Data;
                        if (!string.IsNullOrWhiteSpace(content))
                        {
                            ffoutput.AppendLine(content);
                        }
                    }, (s, e) =>
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
                            if (overwrite)
                            {
                                moving_files.Add(file);
                                File.Move(tmp, file, true);
                                moving_files.Remove(file);

                                processed.Add(file);

                                using FileStream fs = new(Path.Combine(workspace_dir, $"settings.json"), FileMode.Create, FileAccess.Write, FileShare.None);
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
                        }
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
        });
    }


}
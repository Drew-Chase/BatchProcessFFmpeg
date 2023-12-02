// LFInteractive LLC. - All Rights Reserved

using BatchProcessFFmpeg.Models;
using Chase.FFmpeg.Extra;
using CLMath;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.IO.Compression;

namespace BatchProcessFFmpeg.Handlers;

internal class IOHandler
{
    private static IOHandler Instance = Instance ??= new();

    private List<ProcessedFile> _done;
    private string _done_file;
    private Dictionary<string, long> _files;
    private ManifestFile _manifest;
    private string _manifestFile;
    private string _path;
    private string _todoFile;
    private AdvancedTimer _updateTimer;

    private FileSystemWatcher? watcher = null;

    private IOHandler()
    {
        _path = Environment.CurrentDirectory;
        _files = new();
        _done = new();
        _done_file = Path.Combine(ConfigHandler.Instance.WorkspaceDirectory, "done");
        _todoFile = Path.Combine(ConfigHandler.Instance.WorkspaceDirectory, "todo");
        _manifestFile = Path.Combine(ConfigHandler.Instance.WorkspaceDirectory, "manifest.json");

        GetFiles(_path);
        _updateTimer = new(TimeSpan.FromMinutes(5))
        {
            AutoReset = true,
            Interuptable = true,
        };
        void update()
        {
            SaveToDo();
            SaveDone();
            SaveManifest();
        }
        update();
        _updateTimer.Elapsed += (s, e) => update();
        _updateTimer.Start();
        AppDomain.CurrentDomain.ProcessExit += (s, e) => update();
    }

    public static void Complete(ProcessedFile processedFile)
    {
        Instance._done.Add(processedFile);
        Instance.SaveDone();
    }

    public static string GetNextFile()
    {
        string file = Instance._files.First().Key;
        Instance._files.Remove(file);
        return file;
    }

    private void GetFiles(string path)
    {
        _files.Clear();
        try
        {
            Parallel.ForEach(Directory.GetFileSystemEntries(path), file =>
            {
                if (new FileInfo(file).Attributes.HasFlag(FileAttributes.Directory))
                {
                    GetFiles(file);
                }
                else
                {
                    _files.Add(file, new FileInfo(file).Length);
                }
            });
        }
        catch { }
    }

    private void LoadDone()
    {
        using FileStream fs = new(_done_file, FileMode.Open, FileAccess.Read, FileShare.None);
        using GZipStream zip = new(fs, CompressionMode.Decompress);
        using StreamReader reader = new(zip);
        _done = JObject.Parse(reader.ReadToEnd()).ToObject<List<ProcessedFile>>() ?? new List<ProcessedFile>();
    }

    private void LoadTodo()
    {
        using FileStream fs = new(_todoFile, FileMode.Open, FileAccess.Read, FileShare.None);
        using GZipStream zip = new(fs, CompressionMode.Decompress);
        using StreamReader reader = new(zip);
        _files = JObject.Parse(reader.ReadToEnd()).ToObject<Dictionary<string, long>>() ?? new Dictionary<string, long>();
    }

    private void SaveDone()
    {
        using FileStream fs = new(_done_file, FileMode.Create, FileAccess.Write, FileShare.None);
        using GZipStream zip = new(fs, CompressionLevel.SmallestSize);
        using StreamWriter writer = new(zip);
        writer.Write(JsonConvert.SerializeObject(_done));
    }

    private void SaveManifest()
    {
        using FileStream fs = new(_manifestFile, FileMode.Create, FileAccess.Write, FileShare.None);
        using StreamWriter writer = new(fs);
        writer.Write(JsonConvert.SerializeObject(_manifest, Formatting.Indented));
    }

    private void SaveToDo()
    {
        using FileStream fs = new(_todoFile, FileMode.Create, FileAccess.Write, FileShare.None);
        using GZipStream zip = new(fs, CompressionLevel.SmallestSize);
        using StreamWriter writer = new(zip);
        writer.Write(JsonConvert.SerializeObject(_files));
    }

    private Task Watch() => Task.Run(() =>
                        {
                            watcher = new()
                            {
                                Path = Environment.CurrentDirectory,
                                NotifyFilter = NotifyFilters.CreationTime | NotifyFilters.DirectoryName | NotifyFilters.FileName,
                                Filter = "*.*",
                                IncludeSubdirectories = true,
                                EnableRaisingEvents = true,
                            };
                            void handler(object s, FileSystemEventArgs e)
                            {
                                try
                                {
                                    if (!_done.Any(i => i.file.Equals(e.FullPath, StringComparison.OrdinalIgnoreCase)) && FFVideoUtility.video_extension.Contains(new FileInfo(e.FullPath).Extension.Trim('.')))
                                    {
                                        if (e.ChangeType == WatcherChangeTypes.Deleted && _files.ContainsKey(e.FullPath))
                                        {
                                            _files.Remove(e.FullPath);
                                        }
                                        if (e.ChangeType == WatcherChangeTypes.Created && !_files.ContainsKey(e.FullPath))
                                        {
                                            _files.Add(e.FullPath, new FileInfo(e.FullPath).Length);
                                            _files = _files.OrderByDescending(i => i.Value).ToDictionary(i => i.Key, i => i.Value);
                                        }

                                        ConsoleHandler.SendMessage($"Detected filesystem change!\nFile: \"{e.FullPath}\"\nChange Type: {e.ChangeType}", TimeSpan.FromSeconds(10));
                                    }
                                }
                                catch (Exception ex)
                                {
                                    ConsoleHandler.SendError("Had issue ");
                                }
                            }
                            watcher.Created += handler;
                            watcher.Deleted += handler;
                        });
}
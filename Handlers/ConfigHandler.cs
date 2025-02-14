// LFInteractive LLC. - All Rights Reserved

using BatchProcessFFmpeg.Models;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace BatchProcessFFmpeg.Handlers;

internal class ConfigHandler
{
    public static ConfigHandler Instance = Instance ??= new ConfigHandler();
    private readonly string _settings_file;

    private ConfigHandler()
    {
        WorkspaceDirectory = Directory.CreateDirectory(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LFInteractive", "Batch Process FFmpeg", Environment.CurrentDirectory.Replace(Path.DirectorySeparatorChar, '_').Replace(":", ""))).FullName;
        _settings_file = Path.Combine(WorkspaceDirectory, "settings.json");
        Load();
    }

    public SettingsFile Settings { get; private set; }

    public string WorkspaceDirectory { get; init; }

    private void Load()
    {
        if (!File.Exists(_settings_file)) Save();
        try
        {
            using FileStream fs = new(_settings_file, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            using StreamReader reader = new(fs);

            Settings = JObject.Parse(reader.ReadToEnd()).ToObject<SettingsFile>();
        }
        catch
        {
            File.Delete(_settings_file);
            Save();
        }
    }

    private void Save()
    {
        using FileStream fs = new(_settings_file, FileMode.OpenOrCreate, FileAccess.Write);
        using StreamWriter writer = new(fs);
        writer.Write(JsonConvert.SerializeObject(Settings, Formatting.Indented));
    }
}
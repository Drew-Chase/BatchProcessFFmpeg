// LFInteractive LLC. 2021-2024


using System.Text;

namespace BatchProcessFFmpeg.Handlers;

internal class ConsoleHandler
{
    private const ConsoleColor ACCENT_COLOR = ConsoleColor.Cyan;
    private const ConsoleColor ERROR_COLOR = ConsoleColor.Red;
    private const ConsoleColor MESSAGE_COLOR = ConsoleColor.Blue;
    private const ConsoleColor PROGRESS_COLOR = ConsoleColor.Yellow;
    private const ConsoleColor WARN_COLOR = ConsoleColor.DarkYellow;
    private static ConsoleHandler Instance = Instance ??= new();
    private readonly List<string> messages;
    private readonly List<string> errors;
    private readonly long start_time;
    private bool show_stats;

    private ConsoleHandler()
    {
        start_time = DateTime.Now.Ticks;
        errors = messages = [];
        show_stats = true;
    }

    public static void HideStats()
    {
        Instance.show_stats = false;
    }

    public static void SendMessage(string messages, TimeSpan? duration = null)
    {
        Instance.messages.Add(messages);
        if (duration != null)
        {
            Task.Run(() =>
            {
                Thread.Sleep((int)duration.Value.TotalMilliseconds);
                Instance.messages.Remove(messages);
            });
        }
    }

    public static void SendError(string messages, TimeSpan? duration = null)
    {
        Instance.errors.Add(messages);
        if (duration != null)
        {
            Task.Run(() =>
            {
                Thread.Sleep((int)duration.Value.TotalMilliseconds);
                Instance.errors.Remove(messages);
            });
        }
    }

    public static void ShowStats()
    {
        Instance.show_stats = true;
    }

    public static void Start()
    {
    }

    public static void ToggleStatsVisibility()
    {
        if (Instance.show_stats)
        {
            HideStats();
        }
        else
        {
            ShowStats();
        }
    }

    private static string GetTime(TimeSpan span)
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

    private async Task Update()
    {
        Console.Clear();
        Console.CursorTop = 0;
        Console.CursorLeft = 0;

        UpdateTitle();
        UpdateProgressBars();
        if (show_stats)
            UpdateStats();
        UpdateMessage();

        await Task.Delay(1000).ConfigureAwait(true);
        await Update().ConfigureAwait(true);
    }

    private void UpdateMessage()
    {
    }

    private void UpdateProgressBars()
    {
    }

    private void UpdateStats()
    {
        // Runtime
        Console.ForegroundColor = MESSAGE_COLOR;
        Console.Write("Runtime: ");
        Console.ForegroundColor = ACCENT_COLOR;
        Console.WriteLine(GetTime(new(DateTime.Now.Ticks - start_time)));

        // Needs Refresh
        Console.ForegroundColor = MESSAGE_COLOR;
        Console.Write("Needs Refresh: ");
        Console.ForegroundColor = ACCENT_COLOR;
        Console.WriteLine(GetTime(new(DateTime.Now.Ticks - start_time)));
    }

    private void UpdateTitle()
    {
        Console.ForegroundColor = ConsoleColor.Green;
        Console.Write("PROCESSING: ");

        string path = new DirectoryInfo(Environment.CurrentDirectory).Name;
        int path_length = 20;

        string title;
        if (Environment.CurrentDirectory.Length >= path_length + path.Length + 4)
            title = $"{new string(Environment.CurrentDirectory.Take(path_length).ToArray())}...\\{path}\\";
        else
            title = Environment.CurrentDirectory;

        Console.ForegroundColor = ACCENT_COLOR;
        Console.WriteLine(title + "\n");
    }
}
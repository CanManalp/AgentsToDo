using System.IO;
using System.Text.Json;

namespace TodoPad;

/// <summary>Window place and view choices, kept in %LOCALAPPDATA%\TodoPad\settings.json.</summary>
public sealed class Settings
{
    static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TodoPad", "settings.json");

    static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public double? Left { get; set; }
    public double? Top { get; set; }
    public double Width { get; set; } = 340;
    public double Height { get; set; } = 480;
    public bool InFront { get; set; } = true;
    public List<string> Collapsed { get; set; } = [];

    /// <summary>Path of the todo file. Empty means %USERPROFILE%\todo.md.</summary>
    public string? TodoFile { get; set; }

    public string ResolveTodoFile() =>
        string.IsNullOrWhiteSpace(TodoFile)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "todo.md")
            : Environment.ExpandEnvironmentVariables(TodoFile);

    public static Settings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath), JsonOptions) ?? new Settings();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // A broken settings file only costs the remembered place; start with defaults.
        }
        return new Settings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, JsonOptions));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Not fatal: the note keeps working, it just won't remember this change.
        }
    }
}

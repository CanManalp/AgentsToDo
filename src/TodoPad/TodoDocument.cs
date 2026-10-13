using System.ComponentModel;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace TodoPad;

/// <summary>
/// The parsed todo file. Format: an optional "# Title", "## Group" headings,
/// and "- [ ] text" / "- [x] text" checklist lines. Everything else is ignored.
/// </summary>
public sealed partial class TodoDocument
{
    static readonly byte[] Utf8Bom = [0xEF, 0xBB, 0xBF];

    [GeneratedRegex(@"^\s*[-*+]\s+\[(?<mark>[ xX])\]\s?(?<text>.*)$")]
    private static partial Regex ItemPattern();

    [GeneratedRegex(@"<!--(?<note>.*?)-->")]
    private static partial Regex NotePattern();

    public string Title { get; private set; } = "To do";
    public List<TodoGroup> Groups { get; } = [];

    public static TodoDocument Parse(string content)
    {
        var document = new TodoDocument();
        bool titleSeen = false;
        TodoGroup? current = null;
        string[] lines = content.Split('\n');

        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i].TrimEnd('\r');

            if (!titleSeen && line.StartsWith("# "))
            {
                document.Title = line[2..].Trim();
                titleSeen = true;
                continue;
            }

            if (line.StartsWith("## "))
            {
                current = new TodoGroup(line[3..].Trim());
                document.Groups.Add(current);
                continue;
            }

            Match match = ItemPattern().Match(line);
            if (!match.Success)
                continue;

            if (current is null)
            {
                current = new TodoGroup("");
                document.Groups.Add(current);
            }

            current.Items.Add(CreateItem(line, i, match));
        }

        document.Groups.RemoveAll(group => group.Items.Count == 0);
        return document;
    }

    static TodoItem CreateItem(string line, int lineIndex, Match match)
    {
        string text = match.Groups["text"].Value;
        string note = string.Join("\n", NotePattern().Matches(text)
            .Select(m => m.Groups["note"].Value.Trim())
            .Where(n => n.Length > 0));
        text = NotePattern().Replace(text, "").Trim();

        bool important = text.StartsWith('!');
        if (important)
            text = text[1..].TrimStart();

        return new TodoItem
        {
            RawLine = line,
            LineIndex = lineIndex,
            Text = text,
            Note = note.Length > 0 ? note : null,
            IsDone = match.Groups["mark"].Value != " ",
            IsImportant = important,
        };
    }

    /// <summary>
    /// Ticks or unticks one item in the file. Only the character between the brackets
    /// changes, so line endings and every other line stay as they were.
    /// Returns false when the line is no longer in the file (it was edited meanwhile).
    /// </summary>
    public static bool SetDone(string path, TodoItem item, bool done)
    {
        (string content, bool hasBom) = ReadText(path);
        string[] lines = content.Split('\n');

        int index = item.LineIndex < lines.Length && lines[item.LineIndex].TrimEnd('\r') == item.RawLine
            ? item.LineIndex
            : Array.FindIndex(lines, l => l.TrimEnd('\r') == item.RawLine);
        if (index < 0)
            return false;

        string line = lines[index];
        int mark = ItemPattern().Match(line.TrimEnd('\r')).Groups["mark"].Index;
        lines[index] = string.Concat(line.AsSpan(0, mark), done ? "x" : " ", line.AsSpan(mark + 1));

        WriteText(path, string.Join('\n', lines), hasBom);
        return true;
    }

    public static (string Content, bool HasBom) ReadText(string path)
    {
        // Share everything: an editor or Claude may have the file open at the same moment.
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        byte[] bytes = buffer.ToArray();

        bool hasBom = bytes.AsSpan().StartsWith(Utf8Bom);
        return (Encoding.UTF8.GetString(bytes.AsSpan(hasBom ? Utf8Bom.Length : 0)), hasBom);
    }

    public static void WriteText(string path, string content, bool hasBom)
    {
        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
        if (hasBom)
            stream.Write(Utf8Bom);
        stream.Write(Encoding.UTF8.GetBytes(content));
    }
}

public sealed class TodoGroup(string name) : INotifyPropertyChanged
{
    bool isCollapsed;

    public string Name { get; } = name;
    public bool HasHeading => Name.Length > 0;
    public List<TodoItem> Items { get; } = [];
    public int OpenCount => Items.Count(item => !item.IsDone);
    public string CountText => OpenCount > 0 ? OpenCount.ToString() : "done";

    public bool IsCollapsed
    {
        get => isCollapsed;
        set
        {
            if (isCollapsed == value)
                return;
            isCollapsed = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsCollapsed)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

public sealed class TodoItem
{
    /// <summary>The line exactly as it was read, used to find it again when writing.</summary>
    public required string RawLine { get; init; }
    public required int LineIndex { get; init; }
    public required string Text { get; init; }
    /// <summary>Text from &lt;!-- --&gt; comments; shown only in the tooltip.</summary>
    public string? Note { get; init; }
    public bool IsDone { get; init; }
    /// <summary>The item text starts with "!".</summary>
    public bool IsImportant { get; init; }

    public string DisplayText => IsImportant ? "! " + Text : Text;
    public string ToolTipText => Note is null ? Text : Text + "\n\n" + Note;
}

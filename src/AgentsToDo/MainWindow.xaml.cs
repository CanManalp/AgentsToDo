using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Rectangle = System.Windows.Shapes.Rectangle;

namespace AgentsToDo;

public partial class MainWindow : Window
{
    const string StarterContent = """
        # To do

        ## Getting started
        - [ ] Drag the note by its brown board
        - [ ] Tick a box when something is done
        - [ ] ! Lines that start with ! are written in red
        - [ ] Hover a line to read all of it <!-- Text inside HTML comments shows up here, in the tooltip. -->

        """;

    readonly Settings settings = Settings.Load();
    readonly string todoPath;
    readonly DispatcherTimer reloadTimer = new() { Interval = TimeSpan.FromMilliseconds(200) };
    readonly FileSystemWatcher? watcher;
    int readFailures;
    TextBox? selectedLine;

    public MainWindow()
    {
        InitializeComponent();
        Icon = PadIcon.Create();
        todoPath = settings.ResolveTodoFile();
        ApplySavedBounds();

        reloadTimer.Tick += (_, _) =>
        {
            reloadTimer.Stop();
            Reload(createIfMissing: false);
        };

        string? folder = Path.GetDirectoryName(todoPath);
        if (folder is not null && Directory.Exists(folder))
        {
            watcher = new FileSystemWatcher(folder, Path.GetFileName(todoPath))
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
            };
            watcher.Changed += OnFileEvent;
            watcher.Created += OnFileEvent;
            watcher.Deleted += OnFileEvent;
            watcher.Renamed += OnFileEvent;
            watcher.EnableRaisingEvents = true;
        }

        Loaded += (_, _) =>
        {
            ApplyLayerMode();
            ApplyDoneMode();
            Reload(createIfMissing: true);
        };
        Deactivated += (_, _) =>
        {
            EndSelecting();
            if (!settings.InFront)
                SendToBack();
        };
        // A click anywhere outside the selected line ends the selection.
        PreviewMouseLeftButtonDown += (_, _) =>
        {
            if (selectedLine is { IsMouseOver: false })
                EndSelecting();
        };
        Closing += (_, _) => SaveBounds();
        Closed += (_, _) => watcher?.Dispose();
    }

    // The watcher runs on a worker thread and often fires several times for one save,
    // so every event just restarts a short timer on the UI thread.
    void OnFileEvent(object sender, FileSystemEventArgs e) =>
        Dispatcher.InvokeAsync(() =>
        {
            reloadTimer.Stop();
            reloadTimer.Start();
        });

    void Reload(bool createIfMissing)
    {
        TodoDocument document;
        try
        {
            // Only on start: later, a missing file usually means an editor is in the middle of saving it.
            if (createIfMissing && !File.Exists(todoPath))
                TodoDocument.WriteText(todoPath, StarterContent, hasBom: false);

            document = TodoDocument.Parse(TodoDocument.ReadText(todoPath).Content);
            readFailures = 0;
        }
        catch (IOException) when (++readFailures < 5)
        {
            // Usually another program is still writing the file; try again shortly.
            reloadTimer.Start();
            return;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            readFailures = 0;
            ShowMessage($"Can't read {todoPath}. {ex.Message}");
            return;
        }

        foreach (TodoGroup group in document.Groups)
            group.IsCollapsed = settings.Collapsed.Contains(group.Name);

        int total = document.Groups.Sum(group => group.Items.Count);
        int open = document.Groups.Sum(group => group.OpenCount);

        // Hidden done items stay in the file; they are only left off the note.
        if (!settings.ShowDone)
        {
            foreach (TodoGroup group in document.Groups)
                group.Items.RemoveAll(item => item.IsDone);
            document.Groups.RemoveAll(group => group.Items.Count == 0);
        }

        TitleText.Text = document.Title;
        CountText.Text = total == 0 ? "" : open == 0 ? "all done" : $"{open} open";
        GroupList.ItemsSource = document.Groups;

        if (total == 0)
            ShowMessage($"Nothing on the list yet. Add a line like \"- [ ] Renew the domain\" to {todoPath}");
        else
            Message.Visibility = Visibility.Collapsed;
    }

    void ShowMessage(string text)
    {
        GroupList.ItemsSource = null;
        Message.Text = text;
        Message.Visibility = Visibility.Visible;
    }

    void Item_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox { DataContext: TodoItem item } box)
            return;

        bool done = box.IsChecked == true;
        box.IsEnabled = false;

        Rectangle? strike = (box.Parent as Grid)?.Children.OfType<Grid>().FirstOrDefault()?
            .Children.OfType<Rectangle>().FirstOrDefault();

        if (done && strike is not null && SystemParameters.ClientAreaAnimation)
        {
            // Draw the pen line across the text first, then save.
            var scale = new ScaleTransform(0, 1);
            strike.RenderTransform = scale;
            var draw = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(260))
            {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
            };
            draw.Completed += (_, _) => SaveDone(item, done);
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, draw);
        }
        else
        {
            SaveDone(item, done);
        }
    }

    // Double-click a line to select it. All of it starts selected, so Ctrl+C copies the whole item,
    // including the part cut off with "…".
    void ItemText_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount != 2 || sender is not Grid cell
            || cell.Children.OfType<TextBox>().FirstOrDefault() is not { } line)
            return;

        e.Handled = true;
        EndSelecting();
        selectedLine = line;
        line.Visibility = Visibility.Visible;
        line.Focus();
        // Select from the end back to the start (like Shift+Home): the caret ends at the start,
        // so the line keeps showing its beginning instead of scrolling to its end.
        // The box was hidden until now, and line-based selection needs it laid out first.
        line.UpdateLayout();
        line.CaretIndex = line.Text.Length;
        EditingCommands.SelectToLineStart.Execute(null, line);
    }

    void SelectableLine_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape)
            return;

        e.Handled = true;
        EndSelecting();
    }

    void EndSelecting()
    {
        if (selectedLine is null)
            return;

        selectedLine.Visibility = Visibility.Collapsed;
        selectedLine = null;
        Keyboard.ClearFocus();
    }

    void SaveDone(TodoItem item, bool done)
    {
        string? error = null;
        try
        {
            // False means the line changed in the meantime; the reload below shows the file as it is now.
            TodoDocument.SetDone(todoPath, item, done);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            error = "not saved, file is busy";
        }

        Reload(createIfMissing: false);
        if (error is not null)
            CountText.Text = error;
    }

    void Group_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: TodoGroup group })
            return;

        group.IsCollapsed = !group.IsCollapsed;
        settings.Collapsed.RemoveAll(name => name == group.Name);
        if (group.IsCollapsed)
            settings.Collapsed.Add(group.Name);
        settings.Save();
    }

    void DoneButton_Click(object sender, RoutedEventArgs e)
    {
        settings.ShowDone = !settings.ShowDone;
        settings.Save();
        ApplyDoneMode();
        Reload(createIfMissing: false);
    }

    void ApplyDoneMode()
    {
        var ink = (Brush)FindResource("InkBrush");
        var paper = (Brush)FindResource("PaperBrush");

        // A filled box means the done items are on the note.
        DoneBox.Fill = settings.ShowDone ? ink : paper;
        DoneTick.Stroke = settings.ShowDone ? paper : ink;
        DoneButton.ToolTip = settings.ShowDone
            ? "Done items are shown. Click to hide them."
            : "Done items are hidden. Click to show them.";
    }

    void LayerButton_Click(object sender, RoutedEventArgs e)
    {
        settings.InFront = !settings.InFront;
        settings.Save();
        ApplyLayerMode();
    }

    void ApplyLayerMode()
    {
        var ink = (Brush)FindResource("InkBrush");
        var paper = (Brush)FindResource("PaperBrush");

        Topmost = settings.InFront;
        // The filled square is this note: in front of the other window, or behind it.
        FrontSquare.Fill = settings.InFront ? ink : paper;
        BackSquare.Fill = settings.InFront ? paper : ink;
        LayerButton.ToolTip = settings.InFront
            ? "Stays in front of other windows. Click to keep it behind them."
            : "Stays behind other windows. Click to keep it in front.";

        if (!settings.InFront)
            SendToBack();
    }

    void SendToBack()
    {
        IntPtr handle = new WindowInteropHelper(this).Handle;
        if (handle != IntPtr.Zero)
            NativeMethods.SetWindowPos(handle, NativeMethods.HwndBottom, 0, 0, 0, 0,
                NativeMethods.SwpNoMove | NativeMethods.SwpNoSize | NativeMethods.SwpNoActivate);
    }

    void MinimizeButton_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    void Board_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState != MouseButtonState.Pressed)
            return;

        DragMove();
        SaveBounds();
    }

    void ResizeGrip_DragDelta(object sender, DragDeltaEventArgs e)
    {
        Width = Math.Max(MinWidth, Width + e.HorizontalChange);
        Height = Math.Max(MinHeight, Height + e.VerticalChange);
    }

    void ResizeGrip_DragCompleted(object sender, DragCompletedEventArgs e) => SaveBounds();

    // Whole rings only: the ring strip is always a multiple of one ring's width.
    void Root_SizeChanged(object sender, SizeChangedEventArgs e) =>
        Rings.Width = Math.Max(26, Math.Floor((Root.ActualWidth - 36) / 26) * 26);

    void ApplySavedBounds()
    {
        Width = Math.Max(MinWidth, settings.Width);
        Height = Math.Max(MinHeight, settings.Height);

        var screens = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
            SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
        if (settings.Left is double left && settings.Top is double top
            && screens.Contains(new Point(left + Width / 2, top + 40)))
        {
            Left = left;
            Top = top;
            return;
        }

        // First start, or the saved place is on a screen that is gone: top right of the main screen,
        // low enough to keep the close buttons of maximized windows clear.
        Rect work = SystemParameters.WorkArea;
        Left = work.Right - Width - 16;
        Top = work.Top + 44;
    }

    void SaveBounds()
    {
        Rect bounds = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
        if (bounds.IsEmpty)
            return;

        settings.Left = bounds.Left;
        settings.Top = bounds.Top;
        settings.Width = bounds.Width;
        settings.Height = bounds.Height;
        settings.Save();
    }
}
